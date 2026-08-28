# Project Progress

Last updated: 2026-08-28
Branch: codex/file-tools

## Current Work

- Post-D7 usability follow-up is implemented on branch `codex/file-tools` and
  is ready for review. The portable file-tool contract now follows Claude
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
- Next curriculum step: D8 multi-agent coordinator with isolated worker context,
  condensed summaries, single-threaded writes, and measured token multiple.
- `CLAUDE.md`, `AGENTS.md`, and `.codex/` were audited together: `CLAUDE.md` is
  canonical, `AGENTS.md` is a minimal Codex bootstrap, and the Codex hook reuses
  the existing Claude Code progress hook.

## Recent Commits

- `814a465 D7: add context compaction pipeline` (PR #8 head before this progress update)
- `b50dd6b D6: context assembly — three-layer prefix (a/b/c)` (merged PR #7)
- `e3b52a6 D5: permission pipeline — pluggable policy + confirmation, fail-closed` (merged PR #6)
- `11b6aed fix(BashTool): complete output channel on stream EOF, not Process.Exited` (merged PR #5)

## Uncommitted Changes

`(clean after committing this progress update)`

## Source Files

```
src/Astra.Core/AgentLoop.cs
src/Astra.Core/Compaction/*
src/Astra.Core/Context/*
src/Astra.Core/Files/*
src/Astra.Core/Permissions/*
src/Astra.Core/PowerShellTool.cs
src/Astra.Core/ITool.cs
src/Astra.Core/ToolBatching.cs
src/Astra.Providers/ChatClientFactory.cs
src/Astra.Cli/AgentApp.cs
src/Astra.Cli/Program.cs
samples/CompactionDemo/*
tests/Astra.Core.Tests/*
```
