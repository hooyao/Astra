# Project Progress

Last updated: 2026-09-02
Branch: main

## Current Work

- Fixed the D8 payoff harness after a learner run exposed a recoverable `Read`
  failure being treated as terminal. `AgentLoop` already returned tool failures
  to the model for correction, but `AgentEvent.Error` did not distinguish that
  path from a terminal agent failure. The event protocol now exposes typed
  `ToolFailure`; `MultiAgentDemo` logs it and keeps iterating, prints tool
  arguments for diagnosis, and documents repository-root path semantics. A
  deterministic regression proves `ToolUse -> ToolFailure -> ToolResult ->
  TextDelta`. The exact parent-root command then completed against
  `gpt-5.6-sol`: single agent 26,386 tokens/22.3s versus multi-agent 152,934
  tokens/65.1s (5.80x tokens), with two workers complete and no isolation-marker
  leak. The learner still needs to rerun the payoff after the fix.
  Verification: 113/113 tests, formatter clean, and zero-warning
  `MultiAgentDemo` Release build.
- Product scope is now explicit: Astra is a Manus-style general autonomous
  agent core, with the coding agent as its first specialization and the
  measured comparison target against Claude Code and Codex. Generic workflow
  engines, model training, application RAG pipelines, intent taxonomies, and
  multi-tenant SaaS control planes are integration concerns rather than
  `Astra.Core` features. New subsystems must pass the documented failure,
  benchmark, ownership, minimal-contract, and compatibility admission gates.
- D8 multi-agent coordination is implemented and assistant-verified; the
  learner-run payoff remains pending. `AgentTool` starts read-only workers with
  clean `AgentLoop` instances, and the CLI batches their terminal reports into
  one escaped user-role notification before synthesis.
- Replaced the implicitly reentrant shared `IWorkerRunner` with explicit
  dependency-injection ownership. `WorkerCoordinator` now holds only an
  `IWorkerSessionFactory`; every admitted worker gets an independent async scope
  containing scoped `IWorker`, `AgentLoop`, provider client, telemetry, and
  private state. Queued workers do not allocate scopes, and each scope is
  disposed before its terminal completion becomes observable. The coordinator
  graph is a separate conversation scope.
- Worker/session execution now uses `Task<WorkerCompletion>` because it is a
  long-running operation whose handle must survive for disposal. The session
  stores and returns the same Task, owns a linked lifetime cancellation source,
  and performs cancel-and-join before scope disposal. This removes the former
  `ValueTask -> Task -> ValueTask` conversion without losing lifecycle control.
- Post-refactor real `gpt-5.6-sol` verification completed both scoped workers
  with actual overlap and no isolation-marker leak. Single-agent: 156,501
  tokens/31.0s; multi-agent: 271,691 tokens/60.1s = 1.74x tokens and 1.94x
  slower. Together with the earlier 0.97x-token run, this shows material
  stochastic tool-use variance while the latency conclusion remains stable.
- Split tool advertisement from execution. `AgentLoop` now retains immutable
  `ToolDefinition` values only; built-in schemas are static readonly, and the
  CLI resolves keyed transient `IToolExecutor` instances after classification
  and permission. Unknown, unused, and denied tools allocate no executor.
  Focused tests prove schema advertisement with zero activation, denial with
  zero activation, and a distinct executor for every admitted invocation.
- Real post-activation `gpt-5.6-sol` verification successfully exercised
  `Glob`, `Grep`, `Read`, and `Agent`; both workers completed and the isolation
  marker stayed absent. The 7.67x-token/1.86x-wall sample reflects an unusually
  short eight-tool-call baseline and is retained as an integration check, not a
  stable cost estimate.
- Replaced CLI configuration indexers and manual options construction with
  strongly typed `IOptions<T>` binding for LLM, compaction, workspace, and
  PowerShell settings. `ConfiguredChatClient`, `ContextCompactor`,
  `WorkspaceFileSystem`, and `PowerShellTool` receive options through constructor
  injection. `CompactionOptionsPostConfigure` derives its output reserve from
  LLM options, while `Enabled=false` is an explicit no-op rather than an absent
  service. `Program.cs` now contains only configuration sources, `AddAstraCli`,
  session-scope creation, and app startup.
- Added typed `WorkerReport` / `WorkerCompletion` contracts, source-generated
  JSON parsing, provider usage aggregation, bounded parallelism, targeted
  cancellation, exactly-once completion fan-in, and a global single-writer
  lane. Invalid reports and exception text are bounded without leaking private
  worker transcripts.
- Twelve focused tests cover context isolation, trusted usage, invalid reports,
  actual read overlap, serialized writers, targeted cancellation, completion
  batching, XML injection escaping, bounded failures, per-worker scope identity
  and disposal, admission-time scope creation, and end-to-end `Agent -> two
  workers -> one synthesis batch` behavior.
- Verification after D8: 112/112 tests, formatter clean, zero-warning Release
  build, and Native AOT publish successful.
- Real `gpt-5.6-sol` payoff: the single agent used 130,251 tokens and 29.7s;
  coordinator plus two workers used 126,416 tokens and 66.3s. The measured token
  multiple was 0.97x and wall time was 2.24x slower. The narrow task did not
  justify multi-agent overhead; the coordinator-only sentinel did not leak.
- Write-capable workers are not exposed in the CLI. The writer lane is present,
  but the learner chose strict stale-version conflicts and atomic same-response
  MultiEdit normalization; those file transaction mechanics must land before
  enabling worker writes.
- Post-D7 usability follow-up is implemented on branch `codex/file-tools` and
  merged via PR #9. The portable file-tool contract follows Claude
  Code's familiar `Read` / `Write` / `Edit` / `Glob` / `Grep` names and core
  fields. `Write` and `Edit` use the CLI `[y/N]` permission gate; all discovery
  tools classify as Read.
- The path policy follows Microsoft Semantic Kernel FileIOPlugin's fail-closed
  restricted shape without adding a Semantic Kernel dependency:
  canonicalization, traversal/UNC/device/symlink escape rejection inside
  configured roots. Absolute local paths are unrestricted by default; repeated
  `--workspace` values opt into a hard multi-root allowlist.
- `Read` now preserves LF, CRLF, and CR rather than rebuilding line endings.
  `Edit` preserves both exact line terminators and a UTF-8 BOM, accepts
  whitespace-only exact matches, remains atomic, and fails without mutation on
  zero or ambiguous matches. `Write` follows the familiar complete-content
  contract, creates missing parent directories, and atomically creates or
  overwrites after confirmation.
- Added native `Glob` and `Grep`. `Glob` uses
  Microsoft.Extensions.FileSystemGlobbing with recursive and brace-expanded
  patterns. `Grep` provides bounded regex content/path/count modes, pagination,
  glob filtering, and 1-based line/column locations. Both skip VCS metadata and
  symbolic links during traversal.
- Real `gpt-5.6-sol` large-file verification used a 30,000-line, 588,899-byte LF
  file with one target at line 23,456. The model independently chose
  `Grep -> Read -> Edit -> (Grep + Read)`; the final byte-level check found
  29,999 LF, zero CR, zero old matches, and one new match.
- A second real run created a file under two missing directories through
  `Write`, then independently chose `Glob -> Read` to find and verify it. The
  resulting file was the exact requested 23 bytes, including one trailing LF.
- `--workspace D:\astra_test` was then verified against the learner's exact
  scenario: startup printed the selected root, the model created
  `hello_world.py` after confirmation, and `Read` returned
  `print("Hello, world!")`.
- Default unrestricted mode was separately verified from the Q: working
  directory by reading the same absolute D: path. Repeated `--workspace` startup
  printed both Q: and D: as the restricted roots.
- Added a dedicated `PowerShellTool` that launches PowerShell directly, streams
  stdout/stderr, reports exit code, and kills the process tree on cancellation.
  It always classifies as Execute and requires confirmation; it is not silently
  constrained by file-tool roots.
- Real `gpt-5.6-sol` verification invoked the tool after `[y/N]` approval and
  returned `POWERSHELL_READY`, PowerShell `7.4.19`, and exit code 0.
- OpenAI's native Responses `apply_patch` is intentionally not imitated by a
  same-named JSON function. The current Microsoft.Extensions.AI/OpenAI SDK path
  exposes function tools but not typed `apply_patch_call` orchestration; that
  provider-native transport integration remains separate work.
- Verification after the follow-up: 94/94 tests, zero-warning Release build,
  formatter clean, and Native AOT publish successful.
- Track D Day 7 compaction is complete in PR #8 on top of merged D6 PR #7; the
  learner directly inspected and confirmed both payoff paths on 2026-08-28.
- Added an explicit `CompactionResult` union (`NotNeeded` / `Applied` /
  `Failed`), rough provider-neutral token estimation, allowlisted
  microcompaction, LLM full compaction with a verbatim recent tail, and atomic
  preflight integration before every model round-trip.
- Added an OpenAI Responses-compatible `IChatClient` adapter. Verified against
  local `gpt-5.6-sol` at `http://localhost:8765/codex` with no credential.
- Verification: 70/70 tests, formatting clean, solution build clean, Native AOT
  publish clean. `samples/CompactionDemo` verifies both deterministic and real
  provider paths.
- Next curriculum step: the learner runs `samples/MultiAgentDemo --real`, reads
  the token/wall-time comparison, and confirms the payoff before D8 is marked
  complete.
- `CLAUDE.md`, `AGENTS.md`, and `.codex/` were audited together: `CLAUDE.md` is
  canonical, `AGENTS.md` is a minimal Codex bootstrap, and the Codex hook reuses
  the existing Claude Code progress hook.

## Recent Commits

- `bfc7f01 docs: define autonomous-agent product scope` (merged PR #11)
- `76676b2 feat: add scoped multi-agent coordination` (merged PR #10)
- `cffdf7a feat: add coding file and PowerShell tools` (merged PR #9)
- `814a465 D7: add context compaction pipeline` (PR #8 head before this progress update)
- `b50dd6b D6: context assembly — three-layer prefix (a/b/c)` (merged PR #7)
- `e3b52a6 D5: permission pipeline — pluggable policy + confirmation, fail-closed` (merged PR #6)
- `11b6aed fix(BashTool): complete output channel on stream EOF, not Process.Exited` (merged PR #5)

## Current Product State

The independent product boundary and feature-admission rules are merged on
`main`. D8 remains implemented and verified; its learner-run payoff is still
pending before the curriculum day is marked complete.

## Source Files

```
src/Astra.Core/AgentLoop.cs
src/Astra.Core/Compaction/*
src/Astra.Core/Coordination/*
src/Astra.Core/Context/*
src/Astra.Core/Files/*
src/Astra.Core/Permissions/*
src/Astra.Core/PowerShellTool.cs
src/Astra.Core/ToolContracts.cs
src/Astra.Core/ToolBatching.cs
src/Astra.Providers/ChatClientFactory.cs
src/Astra.Cli/AgentApp.cs
src/Astra.Cli/Program.cs
samples/CompactionDemo/*
samples/MultiAgentDemo/*
tests/Astra.Core.Tests/*
```
