# CLAUDE.md

This file provides canonical guidance to Claude Code (claude.ai/code) and Codex when working with code in this repository.

## Project Overview

A high-performance autonomous-agent runtime in C#. The product north star is a
**Manus-style general agent core**: a model repeatedly chooses actions, executes
them in an environment, observes the results, externalizes durable state, and
continues until the task is complete. Coding is the first specialization and
the first hard benchmark. Astra aims to surpass Claude Code and Codex on
measured coding-agent outcomes without turning the core into a collection of
unrelated AI application features.

The core stays domain-neutral where that improves reuse, correctness, or
performance. Concrete product requirements come from real autonomous tasks and
the coding specialization, not from framework feature lists or interview-topic
checklists.

Reference materials (git submodules under `refs/`):
- **Architecture analysis**: `refs/claude-reviews-claude/` — 17-chapter deep dive into Claude Code internals
- **Source reference**: `refs/claude-code-sourcemap/restored-src/src/` — original TypeScript source of Claude Code (v2.1.88)
- **General-agent boundary**: Manus, ["Context Engineering for AI Agents: Lessons from Building Manus"](https://manus.im/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus) — action/observation loop, stable action space, sandbox environment, file-backed context, long-horizon focus, and error recovery

## Product Scope and Feature Admission

### North-star capabilities

Astra's product scope is the reusable runtime required by a long-running
autonomous agent and by its coding specialization:

- iterative model/action/observation execution and a typed event protocol;
- stable, policy-controlled capability and tool lifecycles;
- cache-aware context assembly, compaction, and recoverable external state;
- durable task/session state, resumability, and artifact ownership;
- execution-environment and sandbox abstractions;
- permissions, trust provenance, and side-effect boundaries;
- cancellation, failure recovery, retry safety, and idempotency semantics;
- isolated worker coordination where parallelism improves measured outcomes;
- telemetry and evaluation seams needed to measure the runtime;
- MCP, skills, hooks, and provider adapters as capability integrations.

Coding-specific tools and policies may live above the domain-neutral runtime.
The coding agent is not a demo: it is the first product specialization and the
benchmark used to compare Astra with Claude Code and Codex.

### What "surpass" means

Astra does not compete on raw feature count. A change advances the product only
when it improves one or more measured dimensions:

- task success and correctness;
- long-horizon coherence and recovery from failed actions;
- latency, token use, cache hit rate, and resource cost;
- safe handling of side effects and untrusted observations;
- debuggability and operator-visible state;
- extensibility without weakening the core contracts.

### Feature-admission gate

Before adding a production abstraction or subsystem, answer all of these:

1. Which autonomous-agent or coding-agent failure does it solve?
2. What runnable task, regression test, or benchmark proves the failure and the improvement?
3. Why does the behavior belong in Astra rather than in an application, tool, sample, provider adapter, or existing infrastructure package?
4. What is the smallest contract that solves the demonstrated problem without committing the core to one domain?
5. Does it preserve cancellation, Native AOT, deterministic serialization, security boundaries, and existing performance characteristics?

An interview topic, another framework's feature, or a speculative future use
case is not sufficient evidence. Start with an integration or sample when the
ownership boundary is uncertain; promote it only after the runtime requirement
is demonstrated.

### Explicit non-goals

The following do not belong in `Astra.Core` merely because they appear in AI
job descriptions or adjacent frameworks:

- model training, fine-tuning, and training-data pipelines;
- a generic DAG/business-workflow or durable-orchestration engine;
- vector databases, embedding pipelines, document ingestion, or a general RAG stack;
- application-specific intent taxonomies and business routers;
- a multi-tenant SaaS control plane, billing system, or application compliance layer;
- feature-parity work whose only justification is matching LangGraph, Semantic Kernel, CrewAI, or another framework.

Astra must compose with these systems. It may expose tools, adapters, samples,
or narrow optional packages for proven integrations, but it does not reimplement
their product domains. In particular, an external workflow engine may invoke an
Astra agent as a step; that does not make workflow execution part of the agent
core.

## Language Policy

- **Conversation**: Always reply in the same language the user uses.
- **Written artifacts**: All documentation, memory files, `CLAUDE.md`, and `AGENTS.md` must be in **English**, regardless of conversation language.

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

### Current Solution Structure

```
Astra.slnx
├── src/
│   ├── Astra.Core/              # Runtime contracts and current built-in capabilities
│   ├── Astra.Providers/         # Provider adapters and configuration
│   └── Astra.Cli/               # Coding-agent specialization and terminal host
├── tests/
│   └── Astra.Core.Tests/
└── samples/
    ├── ContextAssemblyDemo/
    ├── CompactionDemo/
    └── MultiAgentDemo/
```

Do not pre-create package boundaries such as Workflows, RAG, Hosting, Plugins,
or Evals from a roadmap diagram. Add a project only when implemented behavior
passes the feature-admission gate and the package boundary is justified by real
dependencies and ownership.

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

Tool advertisement and tool execution have different lifetimes. Immutable
metadata exists for the agent session, while an executor is activated only for
an admitted invocation. Permission-relevant behavior is still **classified
per-invocation, not encoded in a type hierarchy**:

```csharp
public enum ToolAction { Read, Write, Execute, Other }   // Other = fail-closed bucket

public abstract record ToolOutput                         // streamed out of a tool
{
    public sealed record Progress(string Text) : ToolOutput;  // live, for the human; not sent to LLM
    public sealed record Result(string Text)   : ToolOutput;  // the one complete tool_result, for the LLM
}

public sealed class ToolDefinition
{
    string Name { get; }
    string Description { get; }
    JsonElement InputSchema { get; }

    // Input-dependent and fail-closed; runs without creating an executor.
    ToolAction Classify(IDictionary<string, object?>? arguments);
}

public interface IToolExecutor
{
    // Streaming: zero+ Progress for the human, one final Result for the LLM.
    IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        CancellationToken ct);
}
```

Key: `ToolDefinition.Classify` is **input-dependent**, not type-dependent. The
`BashTool` definition returns `Read` for `"ls"` and `Execute` for `"rm -rf"` —
one advertised tool, behavior varies by argument. No class hierarchies like
`ReadOnlyTool`. A missing classifier returns `Other`, the strictest bucket.

All built-in schemas are parsed once into static readonly `JsonElement` values.
The CLI registers implementations as keyed transient `IToolExecutor` services.
`AgentLoop` follows this order: definition lookup → classification → permission
→ executor activation → `ExecuteAsync`. Unknown, unused, and denied tools never
create executor instances. Executors do not acquire external resources in their
constructors; files, processes, and buffers are opened inside `ExecuteAsync` and
released within that invocation.

Why a category (`ToolAction`) instead of three bools (`IsReadOnly` /
`IsConcurrencySafe` / `IsDestructive`): the bools force every caller to AND/OR
three predicates and can't name a single class per call. More importantly, keying
permission on the command *string* (as Claude Code does, exact/prefix) re-prompts
on every small argument change — the UX failure that drives users to bypass
permissions entirely. A behavior *class* lets a host approve "all reads" once and
not re-prompt as arguments drift. See `agent/experiments/d02-tool-contract/`
(in the parent repo) for the verified Claude Code source analysis.

> Status: `ToolDefinition`, `IToolExecutor`, and `BashTool` are implemented.
> D2's behavioral classification is retained while executor activation is now
> invocation-time. The two-layer permission model is also implemented (D5):
> `Classify` provides the bulk
> behavior class; `ClassDefaultPolicy` layers per-command exceptions on top;
> `DefaultPermissionEngine` resolves Allow/Deny/Ask through an optional
> `IUserConfirmation` and fails closed in headless mode. Full `InputSchema`
> validation remains deferred. `Classify`'s v1 command set is a small allowlist
> (a demo, not the production classifier).

**Tool orchestration** partitions a turn's tool calls into batches (implemented,
Track D D3 — `ToolBatching.Partition` + batch execution in `AgentLoop`):
- `Classify == Read` invocations coalesce into a concurrent batch (bounded, default 10)
- Write/Execute/Other are barriers: each runs alone, serially

This is a **stable partition, not a sort**: it is the instruction-scheduling /
data-hazard problem (RAR is the only safe reorder; every write is a fence; the
model's emission order is program order). Concurrent tools fan in through one
`Channel<AgentEvent>`; results map back to the LLM in original call order via
`CallId`. See `agent/experiments/d03-tool-orchestration/teaching-notes.md`.

**Workspace file tools** are implemented as a post-D7 usability follow-up. The
public contract follows Claude Code's familiar `Read` / `Write` / `Edit` /
`Glob` / `Grep` names and core input fields; the implementations share one
`WorkspaceFileSystem`. By default relative paths resolve from the working
directory and absolute local paths are unrestricted. One or more repeated
`--workspace` values opt into a hard multi-root allowlist; lexical traversal and
symlink escape outside those roots are then rejected. UNC/device paths remain
unsupported.

`Read` is bounded by line and character limits and preserves LF, CRLF, or CR
terminators exactly. `Grep` provides bounded regex content/path/count modes with
line and column locations; `Glob` supports recursive patterns and brace
alternatives through Microsoft.Extensions.FileSystemGlobbing. Version-control
metadata and symbolic links are skipped during traversal. `Write` creates
missing parent directories and atomically creates or completely overwrites a
UTF-8 file. `Edit` uses exact ordinal replacement, requires one match unless
`replace_all=true`, and preserves original line terminators and a UTF-8 BOM.

The CLI resolves `Write` and `Edit` actions through `DefaultPermissionEngine` +
`ConsoleUserConfirmation` (`[y/N]`). `Read`, `Glob`, and `Grep` run without a
prompt. Tool-level argument validation is implemented; centralized validation
against each tool's complete `InputSchema` remains deferred. OpenAI's native
Responses `apply_patch` item is not represented by a same-named JSON function;
provider-native tool items require a separate transport integration.

**PowerShell tool** is implemented as a dedicated `PowerShellTool`, not by
nesting PowerShell source inside the Windows `cmd.exe /c` path used by
`BashTool`. It launches `pwsh` directly with `-NoLogo -NoProfile
-NonInteractive -Command`, streams stdout/stderr, reports the process exit code,
and kills/reaps the complete process tree on cancellation. Every invocation is
classified as `Execute` and therefore requires confirmation. A general shell is
not constrained by file-tool workspace roots; the confirmation is the explicit
capability boundary.

**Tool assembly pipeline** (NOT yet implemented — Track D D15, when MCP tools
exist): base tools → feature gates → permission rules → deny lists → MCP tools →
sorted (built-in prefix, MCP suffix for cache stability).

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

1. **MicroCompact** — Clear allowlisted old tool-result payloads; preserve call
   IDs and a recent window. Local content clearing runs under token pressure or
   after a 60-minute cold-cache interval.
2. **Session Memory** — Background-maintained summary (deferred to D9).
3. **Full Compact** — LLM-generated summary of completed older turns while the
   current user turn remains verbatim.
4. **Reactive Compact** — Emergency full-compaction trigger after
   `prompt_too_long` (contract represented; provider retry wiring deferred).

> Status: D7 implements `CompactionResult` as an explicit NotNeeded / Applied /
> Failed union, `RoughChatTokenEstimator`, cache-aware allowlisted
> `ContextCompactor` micro/full paths, and atomic history replacement in
> `AgentLoop`. Preflight runs immediately before every model round-trip, so a
> large tool result is compacted before the follow-up call. `Applied` is the only
> outcome that exposes a detached candidate; cancellation or failure leaves the
> original history unchanged. The CLI emits `CompactionCompleted` with ordered
> step metrics. `samples/CompactionDemo` is the runnable deterministic + real
> provider payoff.

### Multi-Agent Coordination

Workers have **complete context isolation** — they cannot see the coordinator's conversation. Communication uses XML embedded in user-role messages:

```xml
<task-notification>
  <task-id>{agentId}</task-id>
  <status>completed</status>
  <result>{agent's final response}</result>
</task-notification>
```

> Status: D8 implements `WorkerReport` / `WorkerCompletion` and one DI-owned
> execution scope per worker. Each scope contains a scoped `IWorker`, `AgentLoop`,
> provider client, telemetry wrapper, and private history; it is created only
> after admission and disposed before terminal completion is published. The
> session stores and returns one `Task<WorkerCompletion>` and disposal performs
> cancel-and-join before releasing the scope. The
> coordinator session separately owns its scoped `AgentLoop`, `WorkerCoordinator`,
> and `AgentTool`. Additional behavior includes bounded JSON report
> parsing through a source-generated serializer, targeted cancellation, bounded
> parallelism, completion batching, and escaped XML notifications. `AgentTool`
> exposes read-only workers to the CLI; multiple Agent calls in one model
> response run concurrently through D3's read batch, then the CLI collects the
> active group outside the main loop and submits one notification batch for
> synthesis. `WorkerCoordinator` has a global single-writer lane, but
> write-capable workers remain unexposed until strict file-version and atomic
> MultiEdit transactions are implemented. `samples/MultiAgentDemo` compares a
> single agent with two real `gpt-5.6-sol` workers and reports the measured token
> multiple.

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

## Dependency Injection and Configuration

The CLI uses Microsoft.Extensions.DependencyInjection and strongly typed
Microsoft.Extensions.Options throughout its runtime graph. `Program.cs` only
selects configuration sources, calls `AddAstraCli`, creates one coordinator
session scope, and starts `AgentApp`.

Configuration sections bind to `IOptions<LlmConfig>`,
`IOptions<CompactionOptions>`, `IOptions<WorkspaceOptions>`, and
`IOptions<PowerShellOptions>`. Validators fail before use, and
`CompactionOptionsPostConfigure` derives the compaction output reserve from the
LLM output limit. `Compaction:Enabled=false` keeps the same DI graph and makes
`ContextCompactor` a cheap explicit no-op; optional service lookup is not used.
The configuration binding source generator is enabled so Native AOT requires no
reflection-based binder fallback.

Runtime services use constructor injection, including keyed `AgentLoop` and
keyed transient tool executors. Do not reintroduce configuration string
indexers, manually assembled options objects, or `GetService` feature gates in
the composition root.

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
- **Product evidence outranks curriculum coverage.** The Product Scope and Feature Admission rules above are authoritative. Do not implement an interview topic or roadmap item until its concrete Astra failure, ownership, and measurement case is written down.
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
