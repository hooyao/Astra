# Architecture Reference Index

Maps each MyClaude module to the Claude Code analysis chapters in `refs/claude-reviews-claude/architecture/`.
Source code reference in `refs/claude-code-sourcemap/restored-src/src/`.

---

## Quick Lookup: Module → Chapters

| MyClaude Module | Primary Chapters | Secondary |
|-----------------|-----------------|-----------|
| **Core** (agent loop) | 01, 10, 11 | 00, 09 |
| **Tools** | 02, 06 | 07 |
| **Providers** | 15 | 01 |
| **Permissions** | 07 | 05 |
| **Plugins** | 04 | 05 |
| **Mcp** | 15 | 04 |
| **Cli** | 12, 14a, 14b | 16 |
| **Sdk** | 13 | 08 |
| Multi-agent | 03, 08 | 01 |
| Hook system | 05 | 07 |
| Session/persistence | 09 | 11 |
| Config/infra | 16 | 12 |
| Telemetry | 17 | — |

---

## Chapter Details

### 00 — Overview (`00-overview.md`)

Global architecture map. Six design philosophies: LLM as brain, tools as capability boundary, context as scarcest resource, zero-trust security, loop-until-done, extensibility determines ceiling.

**When to read**: Starting a new module; need the big picture of how subsystems connect.

---

### 01 — QueryEngine (`01-query-engine.md`)

**Relevance**: MyClaude.Core — the central `while(true)` async generator loop.

Key source: `QueryEngine.ts` (1,296 lines) + `query.ts` (1,730 lines).

**Core concepts**:
- Two layers: QueryEngine (per-session state) + query() (per-turn generator)
- `submitMessage()` lifecycle — entry point for every user turn
- `query()` loop: call LLM → parse tool_use → execute tools → yield events → loop
- `callModel()` — streaming API call with raw SSE (avoids SDK O(n^2) concatenation)
- `withRetry()` — error recovery engine
- `normalizeMessagesForAPI()` — message conversion for provider compatibility
- Cost/token tracking per turn

**Patterns to reuse**:
- AsyncGenerator (`IAsyncEnumerable<T>`) for backpressure and composability
- State snapshot + mutable queue (mutable messages list, immutable snapshot for query)
- Watermark-based error scoping (turn-scoped errors without counters)

**Anti-patterns**:
- Message mutations invalidate prompt cache — always append, never mutate
- Thinking blocks have 3 strict API rules: need budget, can't be last, must persist
- O(n^2) string concatenation in SDK streams — use raw SSE or chunked assembly

---

### 02 — Tool System (`02-tool-system.md`)

**Relevance**: MyClaude.Tools — unified tool interface and orchestration.

Key source: `Tool.ts` (793 lines) + `tools.ts` (390 lines) + 42 tool directories (~6,500 lines).

**Core concepts**:
- Single `Tool` interface with 30+ methods, behavioral flags (isReadOnly, isConcurrencySafe, isDestructive)
- Tool assembly pipeline: base tools → feature gates → permission rules → deny lists → MCP → sorted
- Partition-sort: built-in tools as contiguous prefix, MCP as suffix (prompt cache stability)
- `ToolSearchTool` — deferred loading when tool count exceeds prompt budget
- `runToolUse()` — 13-step execution pipeline
- FileStateCache prevents stale writes (version tracking)
- Large results persist to disk, reference by file path

**Patterns to reuse**:
- Behavioral flags over inheritance (input-dependent, not type-dependent)
- Partition-sort for prompt cache stability
- Deferred tool discovery via search

**Anti-patterns**:
- Deferred tools without discovery → schema-not-sent error (must call ToolSearch first)
- Tool inputs are model-dependent (typed arrays become strings if schema not sent)
- FileEditTool has 8-check validation pipeline — don't skip validations

---

### 03 — Multi-Agent Coordinator (`03-coordinator.md`)

**Relevance**: Multi-agent orchestration in MyClaude.Core.

Key source: `coordinator/coordinatorMode.ts` (370 lines) + `tools/AgentTool/` (14 files).

**Core concepts**:
- Coordinator mode: compile-time feature gate + env var activation
- Complete context isolation — workers can't see coordinator's conversation
- Four-phase workflow: Research → Synthesis → Implementation → Verification
- XML `<task-notification>` in user-role messages for completion signals
- Scratchpad directory for no-permission file sharing between agents
- Fork vs spawn trade-off: fork reuses parent cache, spawn gets clean context

**Anti-patterns**:
- Workers can't see coordinator's conversation — every prompt must be self-contained
- "Never delegate understanding" — coordinator must synthesize, not just forward
- Fork workers sharing parent cache can't use different models

---

### 04 — Plugin System (`04-plugin-system.md`)

**Relevance**: MyClaude.Plugins — discovery, loading, lifecycle.

Key source: `utils/plugins/` (44 files, 18,856 lines).

**Core concepts**:
- Plugin manifest with marketplace integration, dependency resolution, auto-update
- Six skill sources: commands_DEPRECATED → skills → plugin → managed → bundled → mcp
- Skills as "prompt as code" — Markdown + YAML frontmatter
- Built-in plugins use `defaultEnabled` + `isAvailable()` environment detection
- Three-tier token budget degradation for skill listings
- MCP integration via 6 transports: stdio, sse, http, ws, sdk, sse-ide

**Anti-patterns**:
- "managed" (elevated trust) vs "untrusted" (require approval) — policy distinction matters
- Orphan filter detects plugins whose source repo was deleted
- Symlink attack prevention: `O_NOFOLLOW` for bundled skill files
- Path traversal validation with normalization checks

---

### 05 — Hook System (`05-hook-system.md`)

**Relevance**: MyClaude.Core hook infrastructure, MyClaude.Permissions integration.

Key source: `utils/hooks.ts` (5,023 lines) + `utils/hooks/` (17,931 lines).

**Core concepts**:
- 20 event types across tool lifecycle
- PreToolUse is most critical — can approve, deny, modify inputs, inject context, or stop
- 4 hook implementations: command (shell), HTTP (webhook), agent (sub-Claude), function (SDK-only)
- JSON protocol: stdin input, stdout output, exit code convention (0=ok, 1=warn, 2=block)
- Permission Matcher Pipeline using closures
- Aggregation: any "deny" → deny, any "blocking" → stop, context concatenated
- Workspace trust prevents untrusted hooks from executing

**Matcher syntax**: `"ToolName"`, `"ToolName(pattern)"`, `"Bash(git *)"`, `"*"`

---

### 06 — Bash Engine (`06-bash-engine.md`)

**Relevance**: MyClaude.Tools — BashTool implementation.

Key source: `BashTool.tsx` (1,144 lines) + `Shell.ts` (475 lines) + `bash/` parsers (~7,000 lines AST).

**Core concepts**:
- `runShellCommand()` — AsyncGenerator for progress reporting
- Command classification: search, read, write (determines permission level)
- Defense-in-depth: parsing → permission rules → sandbox wrapping → OS enforcement
- Merged stdout/stderr to single fd with O_APPEND atomicity
- CWD tracking via `pwd -P` post-command with Unicode NFC normalization
- Shell environment snapshot/restore between commands
- Size watchdog kills background tasks exceeding max output bytes
- ExtGlob disabled before every command (security against malicious filenames)

**Anti-patterns**:
- Sleep command blocked from auto-backgrounding unless explicit
- Hidden `_simulatedSedEdit` field prevents model from bypassing permission checks
- Process termination uses tree-kill with SIGKILL (no graceful shutdown)
- Bare git repo attack prevention: pre/post-command file scrubbing

---

### 07 — Permission Pipeline (`07-permission-pipeline.md`)

**Relevance**: MyClaude.Permissions — the core security layer.

Key source: `utils/permissions/` (24 files, ~320KB) + `utils/settings/` (17 files, ~135KB).

**Core concepts**:
- Seven-step gauntlet: deny rules → ask rules → tool check → bypass mode → always-allow → passthrough → ask
- Permission modes: default, plan, acceptEdits, bypassPermissions, dontAsk, auto
- Rule sources (priority order): policy → user → project → local → command → session
- YOLO classifier: 2-stage XML system (tool-only transcript, no assistant text)
- Bypass-immune safety checks: 4 checks fire even in bypassPermissions mode
- Denial circuit breakers: 3 consecutive or 20 total → fallback
- OAuth 2.0 PKCE flow for authentication
- Secure credential storage (macOS Keychain, 4096-byte stdin limit caveat)

**Patterns to reuse**:
- Fail-closed default (unknown → deny)
- Short-circuit evaluation (each layer can terminate early)
- Reversible permission stripping (dangerous rules stashed on auto mode entry)
- Sticky latches prevent prompt cache busting from feature toggles

**Anti-patterns**:
- Iron gate feature flag controls fail-closed vs open on classifier API error
- Dangerous bash permissions auto-stripped in auto mode
- Classifier sees tools, not text (prevents social engineering)

---

### 08 — Agent Swarms (`08-agent-swarms.md`)

**Relevance**: MyClaude.Sdk — parallel agent execution.

Key source: `utils/swarm/` (~30 files, ~6.8K lines) + `utils/teammateMailbox.ts` (1,184 lines).

**Core concepts**:
- Team lifecycle: leader creates team → workers spawned → file-based mailbox communication
- 3 backends: tmux panes, iTerm2 splits, in-process fallback
- File-based mailbox with lockfile concurrency (simpler than IPC/WebSocket)
- 7 task state variants: LocalShell, LocalAgent, RemoteAgent, InProcess, Workflow, MonitorMcp, DreamTask
- Permission delegation: worker → leader → worker
- Strict hierarchy: one leader, many workers

**Anti-patterns**:
- TeamAllowedPaths restrict file sharing scope
- Teammates can't see coordinator's conversation (isolated context)
- Fork workers inherit parent context but can't change models
- Shutdown is leader-initiated, worker-approved (graceful cascade)

---

### 09 — Session Persistence (`09-session-persistence.md`)

**Relevance**: MyClaude.Core — conversation storage and resume.

Key source: `utils/sessionStorage.ts` (5,106 lines) + `utils/sessionRestore.ts` (552 lines).

**Core concepts**:
- Append-only JSONL storage, one file per session
- Path: `~/.claude/projects/{sanitized-cwd}/{session-id}.jsonl`
- Parent-UUID chain for resume (walks leaf → root)
- Entry types: user, assistant, system, attachment, summary, custom-title, ai-title, tag, mode, worktree-state
- Dual write paths: async queue (100ms coalescing) + sync direct
- Lite metadata reads 64KB head+tail for session picker
- UUID deduplication prevents duplicate writes
- Cross-project resume generates `cd {path} && claude --resume {id}`

**Anti-patterns**:
- Snip/compact mutations must update parentUuid chains or resume loads orphaned messages
- FileStateCache must clear on resume to prevent stale write detection
- Sidechain messages (subagents) go to separate `agent-{agentId}.jsonl` files

---

### 10 — Context Assembly (`10-context-assembly.md`)

**Relevance**: MyClaude.Core — prompt construction before every LLM call.

Key source: `attachments.ts` (3,998 lines) + `claudemd.ts` (1,480 lines) + `context.ts` (190 lines).

**Core concepts**:
- Three layers: system prompt (static+dynamic) → user/system context (memoized) → per-turn attachments (1s timeout)
- System prompt split at `DYNAMIC_BOUNDARY` for prompt cache stability (`scope: 'global'`)
- CLAUDE.md memory system with 6-layer priority (managed → user → project → local → automem → team)
- 30+ attachment types, each with timeout protection
- @include directives in memory files (recursive, max depth 5)
- Conditional rules with glob-gated frontmatter
- Todo reminders on 10-turn cadence, plan mode on 5-turn cadence

**Anti-patterns**:
- Moving sections across DYNAMIC_BOUNDARY changes caching behavior
- AutoMem/TeamMem files truncated at cap
- Memory surfacing pre-computes headers at attachment time (avoid cache-bust from timestamps)

---

### 11 — Compact System (`11-compact-system.md`)

**Relevance**: MyClaude.Core — context compression for long conversations.

Key source: `compact.ts` (1,706 lines) + `microCompact.ts` (531 lines) + `sessionMemoryCompact.ts` (631 lines).

**Core concepts**:
- Four tiers: MicroCompact → Session Memory Compact → Full Compact → Reactive (emergency)
- MicroCompact: surgical tool result removal (time-based or cache-aware mode)
- Session Memory Compact: uses background agent summary, no LLM call needed
- Full Compact: LLM-generated summary (sub-agent call)
- Reactive: emergency compression on `prompt_too_long`, group-based truncation
- Auto-compact circuit breaker: 3 consecutive failures → stop (prevents API waste)
- Post-compact file restoration: 5 files max, 50K tokens, 5K per file
- Compactable tools: FileRead, Bash, PowerShell, Grep, Glob, WebSearch, WebFetch, FileEdit, FileWrite

**Anti-patterns**:
- Must preserve API invariants: complete tool pairs, non-orphaned thinking blocks
- Compaction must never run from 'session_memory', 'compact', or 'marble_origami' querySource (prevent recursion)
- Auto-compact circuit breaker after 3 failures prevents 250K wasted API calls/day

---

### 12 — Startup & Bootstrap (`12-startup-bootstrap.md`)

**Relevance**: MyClaude.Cli — application startup optimization.

Key source: `cli.tsx` (303 lines) + `init.ts` (341 lines) + `setup.ts` (478 lines) + `state.ts` (1,759 lines).

**Core concepts**:
- 14 fast-path shortcuts before reaching full CLI (--version = zero imports)
- Import-gap parallelism: I/O operations start during module evaluation
- API preconnection overlaps with action-handler work (~100ms each)
- Bootstrap State singleton: 1,759-line global state
- Startup profiler for performance instrumentation
- CA certs must load before first TLS handshake
- Sticky-on latches for cache-stable headers

**Anti-patterns**:
- Settings bootstrap must happen eagerly (tools capture env vars at import time)
- Telemetry (400KB+ OpenTelemetry) deferred until after trust dialog
- Scroll drain suspension prevents background intervals from competing with UI

---

### 13 — Bridge System (`13-bridge-system.md`)

**Relevance**: MyClaude.Sdk — remote control and embedding.

Key source: `bridge/bridgeMain.ts` (3,000 lines) + `bridge/replBridge.ts` (2,407 lines).

**Core concepts**:
- Two modes: standalone bridge (server) vs REPL bridge (in-process)
- 3 spawn modes: single-session, worktree (git isolation), same-dir
- Up to 32 concurrent sessions
- Transport generations: v1 (WebSocket+POST), v2 (SSE+CCRClient), v3 (env-less OAuth)
- Poll-dispatch-heartbeat loop
- JWT refresh scheduling, crash recovery pointers
- Echo dedup via BoundedUUIDSet (2000-entry cap)

---

### 14a — UI & State Management (`14-ui-state-management.md`)

**Relevance**: MyClaude.Cli — terminal UI architecture.

Key source: `ink/` (49 files, ~600KB) + `state/store.ts` (35 lines) + `state/AppState.tsx` (23.5KB).

**Core concepts**:
- Fully forked Ink rendering engine with React 19 ConcurrentRoot
- 35-line closure-based store (replaces Redux/Zustand)
- `useSyncExternalStore` integration with React
- W3C-style capture/bubble event system in terminal
- Yoga Flexbox layout engine for terminal components
- DOM nodes track `dirty` flag and yogaNode

---

### 14b — UI & State Rendering (`14-ui-state-rendering.md`)

**Relevance**: MyClaude.Cli — rendering pipeline.

**Core concepts**:
- Screen buffer: packed Int32 cells (charId 32b + styleId + hyperlinkId + width)
- CharPool/StylePool with ASCII fast-path, non-ASCII via Map (string interning)
- BigInt64Array for bulk fills (zero-GC)
- Double buffering: frontFrame vs backFrame with blit optimization
- Frame scheduling at 16ms (~60fps), coalesces multiple state changes
- Virtual scrolling with WeakMap height cache

---

### 15 — Services & API Layer (`15-services-api-layer.md`)

**Relevance**: MyClaude.Providers + MyClaude.Mcp.

Key source: `services/api/claude.ts` (~126KB, 3,420 lines) + `withRetry.ts` (823 lines).

**Core concepts**:
- Multi-provider client factory: Anthropic 1P, AWS Bedrock, Azure Foundry, Google Vertex
- `queryModel()` — 700-line core orchestrator with GrowthBook kill-switch
- Beta header latching (sticky-on pattern for cache stability)
- `withRetry()` — 823-line retry state machine with exponential backoff
- Fallback model trigger after 3 consecutive 529 errors
- Foreground vs background: 529 retries only for foreground queries
- Persistent retry mode (UNATTENDED_RETRY): indefinite retries with 30s heartbeat chunks
- Client request ID injection via `buildFetch()`

---

### 16 — Infrastructure & Config (`16-infrastructure-config.md`)

**Relevance**: Cross-cutting infrastructure, config system.

Key source: `bootstrap/state.ts` (1,759 lines) + `utils/settings/` (17 files, ~135KB).

**Core concepts**:
- 1,759-line global state singleton with leaf module constraint (ESLint enforced)
- Five-layer settings merge: policy → user → project → local → command → session
- Policy deny rules cannot be overridden by lower-priority sources
- Dual-layer configuration: GlobalConfig + ProjectConfig + SettingsJson
- Secure storage: macOS Keychain (4096-byte stdin limit caveat), plaintext fallback
- Atomic session switching (sessionId + sessionProjectDir always together)
- Signal event primitive for AbortController coordination

---

### 17 — Telemetry & Privacy (`17-telemetry-privacy-operations.md`)

**Relevance**: Observability design reference.

Key source: `services/analytics/` (9 modules, ~148KB).

**Core concepts**:
- Dual-channel: 1P (Protocol Buffers, 10s batch, 200 max, 8K queue) + Datadog (15s, 100 max, 64 types)
- Environment fingerprint: 14+ fields (platform, terminal, CI/CD, VCS, version)
- Repository pseudonymization: SHA256 truncated to 16 chars (not anonymous)
- Tool input truncation: 512 char cap, 4,096 JSON limit, 20 item array cap
- Remote kill-switches via GrowthBook feature flags
- Model codename system (internal → external name mapping)

---

## Cross-cutting Patterns Worth Reusing

1. **AsyncGenerator for backpressure** (ch.01, 06, 15) — `IAsyncEnumerable<T>` in C#
2. **Behavioral flags over inheritance** (ch.02) — input-dependent `IsReadOnly`/`IsConcurrencySafe`
3. **Partition-sort for cache stability** (ch.02) — built-in prefix, MCP suffix
4. **Prompt cache latching** (ch.15, 16) — sticky-on flags prevent cache busting
5. **Leaf module constraint** (ch.16) — prevent circular deps via lint rule
6. **Multi-tier compression** (ch.11) — surgical → summary → full → emergency
7. **Circuit breakers** (ch.07, 11) — auto-compact stops after N failures
8. **File-based IPC** (ch.08) — simpler than WebSocket, crash-safe, debuggable
9. **Closure factories over classes** (ch.15, 16) — `paramsFromContext`, permission matchers
10. **Time-decay for context** (ch.11) — older results compressed more aggressively

## Anti-patterns to Avoid

1. **Message mutation** (ch.01) — invalidates prompt cache; always append
2. **Deferred tools without discovery** (ch.02) — schema-not-sent error
3. **Feature toggles busting cache** (ch.15, 16) — use sticky-on latches
4. **Circular dependencies** (ch.16) — leaf module pattern prevents this
5. **Unbounded retries** (ch.07) — use circuit breakers (3 consecutive / 20 total)
6. **Blocking prompts in headless mode** (ch.07) — must have fallback path
7. **Delegating understanding to sub-agents** (ch.03) — coordinator must synthesize
8. **Compact recursion** (ch.11) — never run compaction from compaction querySource
