# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

A state-of-the-art general-purpose agent framework in C#, inspired by the Claude Code architecture. The primary goal is a **general-purpose agent** that can be specialized into domain-specific agents (coding, research, automation). The coding agent is the first specialization but the framework must remain domain-agnostic.

Reference materials (git submodules under `refs/`):
- **Architecture analysis**: `refs/claude-reviews-claude/` — 17-chapter deep dive into Claude Code internals
- **Source reference**: `refs/claude-code-sourcemap/restored-src/src/` — original TypeScript source of Claude Code (v2.1.88)

## Language Policy

- **Conversation**: Always reply in the same language the user uses.
- **Written artifacts**: All documentation, memory files, and CLAUDE.md must be in **English**, regardless of conversation language.

## Build & Development Commands

```bash
# Restore dependencies
dotnet restore

# Build the entire solution
dotnet build

# Run tests
dotnet test

# Run a single test project
dotnet test tests/Astra.Core.Tests

# Run a specific test by filter
dotnet test --filter "FullyQualifiedName~ToolOrchestration"

# Run the CLI agent
dotnet run --project src/Astra.Cli

# Build release (managed)
dotnet publish src/Astra.Cli -c Release -o dist/

# Build release (Native AOT, single exe, no runtime dependency)
# Requires: Visual Studio C++ build tools (vswhere.exe must be on PATH)
dotnet publish src/Astra.Cli -c Release -r win-x64
# Output: src/Astra.Cli/bin/Release/net10.0/win-x64/publish/Astra.exe
```

## Architecture

### Core Design Principles

1. **LLM as Brain, Harness as Body** — The LLM handles reasoning; the framework manages perception, action, memory, and safety
2. **Tools as Capability Boundaries** — The agent can only do what registered tools allow (fail-closed security)
3. **Context is the Scarcest Resource** — Multi-tier compression is essential, not optional
4. **Loop Until Done** — Not request-response; an iterative `while(true)` tool execution loop
5. **Extensibility via Protocols** — Plugins, Skills, MCP, and hooks enable capability growth without core changes

### Solution Structure

```
Astra.sln
├── src/
│   ├── Astra.Core/              # Agent loop, tool system, context management
│   ├── Astra.Tools/             # Built-in tool implementations
│   ├── Astra.Providers/         # LLM API providers (Anthropic, OpenAI-compat, etc.)
│   ├── Astra.Permissions/       # Permission rules, approval pipeline
│   ├── Astra.Mcp/               # Model Context Protocol client
│   ├── Astra.Plugins/           # Plugin discovery, loading, lifecycle
│   ├── Astra.Cli/               # CLI entry point and terminal UI
│   └── Astra.Sdk/               # Public SDK for embedding agents
├── tests/
│   ├── Astra.Core.Tests/
│   ├── Astra.Tools.Tests/
│   └── Astra.Integration.Tests/
└── samples/
    └── Astra.Samples/           # Example agent configurations
```

### The Agent Loop (Core)

The central abstraction is an **async generator loop** (`IAsyncEnumerable<AgentEvent>`):

```
QueryEngine (per-session)
  └── Query() (per-turn) — async generator
       ├── Trim tool result budgets
       ├── Run compaction (micro → session → full → reactive)
       ├── Check token warnings
       ├── Call LLM API with streaming
       ├── Execute tools (concurrent for read-only, serial for writes)
       ├── Check stop conditions
       ├── Inject attachments (memory, skills, MCP)
       └── State transition → next iteration
```

The query loop is a `while(true)` state machine. Each iteration yields `AgentEvent` items (messages, tool results, state transitions) via `IAsyncEnumerable<T>`. This provides natural backpressure, cancellation via `CancellationToken`, and composability.

### Tool System

Every tool implements a single non-generic interface. Permission-relevant
behavior is **classified per-invocation, not encoded in a type hierarchy**:

```csharp
public enum ToolAction { Read, Write, Execute, Other }   // Other = fail-closed bucket

public abstract record ToolOutput                         // streamed out of a tool
{
    public sealed record Progress(string Text) : ToolOutput;  // live, for the human; not sent to LLM
    public sealed record Result(string Text)   : ToolOutput;  // the one complete tool_result, for the LLM
}

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }

    // Streaming: yield zero+ Progress for the human, exactly one Result (last) for the LLM.
    IAsyncEnumerable<ToolOutput> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct);

    // Input-dependent classification. Default interface method, fail-closed default.
    ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Other;
}
```

Key: `Classify` is **input-dependent**, not type-dependent. `BashTool.Classify`
returns `Read` for `"ls"` and `Execute` for `"rm -rf"` — one type, behavior
varies by argument. No class hierarchies like `ReadOnlyTool`. The fail-closed
default (`Other`, the strictest bucket) lives in the interface as a C# default
interface method, so forgetting to classify is safe, never unsafe.

Why a category (`ToolAction`) instead of three bools (`IsReadOnly` /
`IsConcurrencySafe` / `IsDestructive`): the bools force every caller to AND/OR
three predicates and can't name a single class per call. More importantly, keying
permission on the command *string* (as Claude Code does, exact/prefix) re-prompts
on every small argument change — the UX failure that drives users to bypass
permissions entirely. A behavior *class* lets a host approve "all reads" once and
not re-prompt as arguments drift. See `agent/experiments/d02-tool-contract/`
(in the parent repo) for the verified Claude Code source analysis.

> Status: `ITool` (Name/Description/InputSchema/ExecuteAsync/Classify) and
> `BashTool` are implemented (Track D D2). The two-layer permission model below —
> `Classify` for bulk class decisions, plus a rule engine with a deny-list for
> per-command exceptions — and `ToolContext` / `CheckPermissionsAsync` are
> **not yet built**; they are the permission-layer day's work. `Classify`'s v1
> command set is a small allowlist (a demo, not the production engine).

**Tool orchestration** will partition tool calls per turn (not yet implemented):
- `Classify == Read` invocations can run in parallel
- Write/Execute run serially in a write-exclusive batch

**Tool assembly pipeline**: base tools → feature gates → permission rules → deny lists → MCP tools → sorted (built-in prefix, MCP suffix for cache stability)

### Permission Model (7-Layer Defense-in-Depth)

```
Layer 1: Tool-level validation (schema + ValidateInput)
Layer 2: Permission rule matching (policy → user → project → session)
Layer 3: Domain-specific security (e.g., Bash AST parsing, injection checks)
Layer 4: Speculative classifier (AI side-query: "is this safe?")
Layer 5: User confirmation dialog (interactive mode)
Layer 6: OS-level sandbox (process isolation)
Layer 7: Workspace trust (prevent malicious project configs)
```

Fail-closed: unknown → deny. Each layer can short-circuit.

### Context Management (Three Layers)

| Layer | Lifetime | Cache Strategy |
|-------|----------|----------------|
| System Prompt | Per-session | Static prefix (global cache scope) |
| User Context | Per-session | Memoized once |
| Attachments | Per-turn | Recomputed (with timeout) |

### Compression (Four Tiers)

1. **MicroCompact** — Time-decay old tool results, preserve recent window
2. **Session Memory** — Background-maintained summary (no LLM call)
3. **Full Compact** — LLM-generated summary (sub-agent)
4. **Reactive Compact** — Emergency compression on `prompt_too_long`

### Multi-Agent Coordination

Workers have **complete context isolation** — they cannot see the coordinator's conversation. Communication uses XML embedded in user-role messages:

```xml
<task-notification>
  <task-id>{agentId}</task-id>
  <status>completed</status>
  <result>{agent's final response}</result>
</task-notification>
```

### Hook System

Hooks intercept tool lifecycle via a language-agnostic JSON protocol:
- **Input**: JSON on stdin (session_id, tool_name, tool_input, etc.)
- **Output**: JSON on stdout (continue, decision, updatedInput, etc.)
- **Exit codes**: 0 = success, 1 = non-blocking warning, 2 = blocking error

### MCP Integration

Support stdio, SSE, HTTP, and WebSocket transports. MCP tools appear identical to built-in tools from the LLM's perspective. Use deferred loading (`ToolSearch`) when tool count exceeds prompt budget.

## Key Design Patterns

- **IAsyncEnumerable as communication protocol** — replaces callbacks/events/message bus
- **Behavioral classification over inheritance** — input-dependent `Classify` returning `ToolAction`, not a tool type hierarchy
- **Mutable state with immutable snapshots** — mutable messages list, immutable snapshot for query loop
- **Partition-sort for prompt cache stability** — built-in tools as contiguous prefix, MCP as suffix
- **Watermark-based error scoping** — turn-scoped errors without counters
- **Three-tier token estimation** — rough (fast) → proxy (cheap) → exact (precise), escalate as needed
- **Circuit breakers** — auto-compact stops after 3 failures to prevent API waste

## Target Platform

- **.NET 10** (LTS) — use latest C# language features
- **Native AOT compatible** — no reflection, no `dynamic`, no runtime code generation
  - All serialization via `System.Text.Json` source generators (`JsonSerializerContext`)
  - Avoid `Type.GetType()`, `Activator.CreateInstance()`, `Expression.Compile()`
  - Use `[JsonSerializable]` attributes on all serializable types
  - Trim-safe: no trimmer warnings allowed

## LLM Provider Abstraction

Use **`Microsoft.Extensions.AI.Abstractions`** (`IChatClient`) as the provider interface contract. This is a thin abstraction layer (not a framework) with broad ecosystem adoption.

Provider packages:
| Provider | Package | AOT Status |
|----------|---------|------------|
| OpenAI / Azure OpenAI | `Microsoft.Extensions.AI.OpenAI` | Partial |
| Anthropic Claude | `Anthropic.SDK` | TBD — verify or wrap |
| Google Gemini | `GeminiDotnet.Extensions.AI` | Verified |
| AWS Bedrock | `AWSSDK.Extensions.Bedrock.MEAI` | TBD |
| Ollama / local | `OllamaSharp` | Verified |

If a provider package is not AOT-safe, write a thin AOT-compatible wrapper around its HTTP API rather than pulling in the non-AOT package. All core agent code must remain AOT-clean.

## Architecture Reference

When implementing a subsystem, read `refs/architecture-index.md` for a comprehensive chapter-by-module mapping. That index maps each Astra module to the relevant Claude Code analysis chapters with key design decisions, critical functions, and anti-patterns. Read it on-demand — do not keep it in context permanently.

## Development Methodology

- **Learn from Claude Code, don't copy it.** Claude Code's architecture analysis (`refs/`) is a reference for design decisions and patterns, not a blueprint. Avoid its accumulated tech debt and over-complexity. Design our architecture pragmatically based on actual needs.
- **Every incremental step must produce a working, runnable artifact** — not stubs or skeletons. If it compiles but doesn't do anything useful, it's not done.

## Coding Conventions

- Use latest C# features (primary constructors, collection expressions, etc.)
- Use `CancellationToken` throughout all async paths
- Prefer `ValueTask` for hot paths that often complete synchronously
- `System.Text.Json` source generators only — no reflection-based serialization
- Domain errors as result types (`Result<T, E>`), not exceptions for expected failures
- Keep tool implementations stateless — all state lives in `ToolContext`
- No unsafe casts in production code; use pattern matching for type narrowing
- All public APIs must be trim/AOT annotated (`[DynamicallyAccessedMembers]` etc. where unavoidable)
