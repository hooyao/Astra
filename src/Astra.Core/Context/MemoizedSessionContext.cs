namespace Astra.Core.Context;

/// <summary>
/// Wraps an <see cref="ISessionContextProvider"/> so its inner <c>GetAsync</c> runs
/// <b>exactly once</b> for the session and every later call returns the same string.
/// This is the C# equivalent of Claude Code's lodash <c>memoize</c> around
/// <c>getSystemContext()</c> (src/context.ts:116).
/// </summary>
/// <remarks>
/// D6. Correctness detail: the memo caches the <see cref="Task{TResult}"/>, not the
/// resolved string. If two turns race the very first call, both await the <i>same</i>
/// in-flight task, so the underlying work (e.g. a <c>git status</c> subprocess) runs
/// once, not twice. Caching the string instead would leave a window where the second
/// caller sees no cached value yet and starts a duplicate subprocess.
///
/// The memo is never invalidated per turn — that is the whole point (the value is
/// frozen for the session). Claude Code clears its memo only on explicit events
/// (worktree switch, /memory, compaction); those seams are out of D6 scope.
/// </remarks>
public sealed class MemoizedSessionContext(ISessionContextProvider inner) : ISessionContextProvider
{
    private readonly object _gate = new();
    private Task<string>? _cached;

    public ValueTask<string> GetAsync(CancellationToken ct = default)
    {
        // Fast path: already computed (or in flight) — return the cached task.
        var cached = _cached;
        if (cached is not null)
            return new ValueTask<string>(cached);

        lock (_gate)
        {
            // Start the work exactly once. We intentionally do NOT pass ct into the
            // cached task: the session snapshot must not be tied to the lifetime of
            // whichever turn happened to trigger the first call (if that turn is
            // cancelled, later turns still need the snapshot). Per-call cancellation
            // is honored by the caller awaiting the returned ValueTask.
            _cached ??= inner.GetAsync(CancellationToken.None).AsTask();
            return new ValueTask<string>(_cached);
        }
    }
}
