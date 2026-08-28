# Astra

A general-purpose agent framework in C#. The LLM reasons; the framework perceives, acts, and remembers.

Astra provides the core loop that turns any LLM into an iterative agent — call the model, execute tools, feed results back, repeat until done. It is designed to be specialized into domain-specific agents (coding, research, automation, and beyond) without changing the core.

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

- **Astra.Core** — Agent loop, tool interface, event protocol
- **Astra.Providers** — LLM provider adapters (Azure OpenAI, etc.)
- **Astra.Cli** — Terminal REPL, one of many possible frontends

## Quick Start

```bash
dotnet run --project src/Astra.Cli
```

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

`Write`, `Edit`, and every `powershell` command require interactive
confirmation. PowerShell is a general process capability and is not constrained
by file-tool workspace roots.

## License

MIT
