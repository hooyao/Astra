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

## License

MIT
