namespace Astra.Core.Context;

/// <summary>
/// Layer b of context assembly: session-scoped context (CLAUDE.md, environment,
/// git status, recent commits). Computed <b>once</b> at session start and reused
/// verbatim every turn — this is the memoization from Claude Code's
/// <c>getSystemContext()</c> / <c>getGitStatus()</c> (src/context.ts, wrapped in
/// lodash <c>memoize</c>).
/// </summary>
/// <remarks>
/// D6. Freezing a value that genuinely changes mid-session (git status) is
/// deliberate: it keeps the a+b system prefix <b>byte-stable</b> so the provider's
/// prompt cache keeps hitting it across turns. A mid-session commit or a date
/// rollover must not cascade-evict the whole prefix. The accepted cost is a stale
/// snapshot — Claude Code tells the model so ("this status is a snapshot in time,
/// and will not update during the conversation"). Live state, when needed, is
/// fetched via a tool (just-in-time — Track D D9), not re-read into the prefix.
/// See agent/experiments/d06-context-assembly/source-reconciliation.md.
/// </remarks>
public interface ISessionContextProvider
{
    /// <summary>
    /// Produce this layer's text. Callers must invoke it at most once per session
    /// and cache the result (see <see cref="MemoizedSessionContext"/>); implementations
    /// are free to do real I/O (a subprocess, a file read) since it runs once, at
    /// session start, off the per-turn critical path.
    /// </summary>
    ValueTask<string> GetAsync(CancellationToken ct = default);
}
