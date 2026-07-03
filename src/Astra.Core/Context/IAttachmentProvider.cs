namespace Astra.Core.Context;

/// <summary>
/// Layer c of context assembly: a per-turn attachment. Recomputed <b>every turn</b>,
/// used then discarded — the opposite lifetime of layer b. Concrete providers surface
/// transient, harness-gathered context the model did not ask for: periodic reminders,
/// a skill/tool listing, IDE selection, diagnostics, @-mentioned file content.
/// </summary>
/// <remarks>
/// D6. In Claude Code these are the 30+ attachment types assembled by
/// <c>getAttachments()</c> (attachments.ts), each an external I/O (disk read, IPC to
/// the IDE, a linter subprocess, a network call to an MCP server). Because c is
/// computed on the per-turn critical path — the user has hit enter and is waiting —
/// the whole gather runs under a deadline (see <see cref="AttachmentGatherer"/>): a
/// single hung source must not hold the turn hostage. A provider that returns null,
/// throws, or exceeds the deadline simply contributes nothing this turn; since c is
/// per-turn, the missing content reappears next turn (delayed by one turn, not lost).
/// This is best-effort by construction — the counterpart to Claude Code's
/// <c>maybe()</c> wrapper that catches and logs each source's failure.
/// See agent/experiments/d06-context-assembly/source-reconciliation.md.
/// </remarks>
public interface IAttachmentProvider
{
    /// <summary>A short label for logs/tracing (e.g. "task-reminder", "skill-listing").</summary>
    string Name { get; }

    /// <summary>
    /// Produce this attachment's text for the current turn, or null to contribute
    /// nothing this turn. Implementations should honor <paramref name="ct"/> promptly:
    /// the gatherer cancels it at the per-turn deadline.
    /// </summary>
    ValueTask<string?> GetAsync(CancellationToken ct = default);
}
