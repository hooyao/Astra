# Astra

A high-performance autonomous-agent runtime in C#. The LLM reasons; Astra
manages actions, environments, context, durable task state, and safety.

Astra's north star is a
[Manus-style general agent core](https://manus.im/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus):
call the model, execute an action in an environment, append the observation,
externalize recoverable state, and continue until the task is complete. Coding
is the first product specialization and the first hard benchmark. The goal is
to surpass Claude Code and Codex on measured coding-agent outcomes, not to
accumulate every feature found in adjacent AI frameworks.

## Architecture

```
User Input
    │
    ▼
AgentLoop.SubmitAsync()  ──→  IAsyncEnumerable<AgentEvent>
    │                              │
    ▼                              ▼
while (true) {               Consumer renders events
  Stream LLM response         (Console, HTTP, WebSocket, ...)
  Tool calls? → Execute
  Inject results → Continue
}
```

- **Astra.Core** — Agent loop, tool definitions/executors, event protocol
- **Astra.Providers** — LLM provider adapters (Azure OpenAI, etc.)
- **Astra.Cli** — Terminal REPL, one of many possible frontends

## Product Boundary

The runtime owns the contracts required by autonomous execution: the agent
loop, tool/capability lifecycle, context and compaction, recoverable task state,
environment and sandbox boundaries, permissions and trust provenance,
cancellation and failure recovery, worker isolation, and telemetry/evaluation
seams. Coding-specific tools and policies build on those contracts.

Astra is intentionally not a model-training framework, generic workflow
engine, vector database, document-ingestion/RAG platform, application-specific
intent router, or multi-tenant SaaS control plane. It composes with those
systems through tools and adapters. A feature enters the product only when a
real autonomous or coding task demonstrates the failure, and a runnable test or
benchmark demonstrates the improvement. Interview coverage and framework
feature parity alone do not justify a core abstraction; see the full admission
rules in `CLAUDE.md`.

## Quick Start

```bash
dotnet run --project src/Astra.Cli
```

CLI configuration is bound through strongly typed `IOptions<T>` and validated
when the scoped runtime graph is created. `Program.cs` contains no section-key
lookups or manual options construction; `AddAstraCli` owns the DI registrations.

By default, relative paths resolve from the current working directory and
absolute local paths are unrestricted. Opt into a hard allowlist with one or
more `--workspace` values:

```powershell
dotnet run --project src/Astra.Cli -- --workspace D:\astra_test
dotnet run --project src/Astra.Cli -- --workspace Q:\repo --workspace D:\data
```

The default CLI file tools use the familiar Claude Code contract: `Read`,
`Write`, `Edit`, `Glob`, and `Grep`. `Read` supports bounded line ranges;
`Grep` returns bounded regex matches with line and column locations; `Glob`
supports recursive patterns and brace alternatives. `Write` creates missing
parent directories and writes complete file content, while `Edit` performs a
unique exact-string replacement by default. Reads preserve original line
terminators, and edits preserve both line terminators and a UTF-8 BOM.

Tool metadata is immutable and available for model advertisement without an
executor instance. Built-in executors are keyed transient services: Astra
creates one only after the model requests that tool and the permission pipeline
admits the call. Unused and denied tools are never instantiated.

`Write`, `Edit`, and every `powershell` command require interactive
confirmation. PowerShell is a general process capability and is not constrained
by file-tool workspace roots.

The CLI also exposes `Agent` for substantial read-only research. Each worker
gets an independent dependency-injection scope containing its own provider
client, `AgentLoop`, telemetry, and private history, so it cannot see the
coordinator conversation. Prompts must therefore be self-contained. Emit
multiple independent `Agent` calls in one response to run them concurrently.
Their bounded reports return as escaped task-notification user messages and are
synthesized in one follow-up turn.

Run the D8 single-agent versus two-worker comparison with:

```powershell
dotnet run --project samples/MultiAgentDemo -c Release -- --real
```

## License

MIT
