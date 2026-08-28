using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Astra.Core.Compaction;

/// <summary>
/// The explicit outcome of one context-compaction preflight. No nullable value
/// or boolean carries control flow: every caller must handle all three states.
/// </summary>
public abstract record CompactionResult
{
    private CompactionResult() { }

    /// <summary>The current context is safe and no message changed.</summary>
    public sealed record NotNeeded(
        int InputTokens,
        int ThresholdTokens) : CompactionResult;

    /// <summary>
    /// Compaction succeeded. <paramref name="CandidateMessages"/> is a detached
    /// collection that the caller may atomically install as its active history.
    /// </summary>
    public sealed record Applied(
        ImmutableArray<ChatMessage> CandidateMessages,
        CompactionReport Report) : CompactionResult;

    /// <summary>
    /// An expected compaction failure. The original history remains authoritative;
    /// no partial candidate is exposed to the caller.
    /// </summary>
    public sealed record Failed(
        CompactionFailure Failure,
        int InputTokens,
        int ThresholdTokens) : CompactionResult;
}

/// <summary>Why this compaction attempt ran.</summary>
public enum CompactionTrigger
{
    Automatic,
    Reactive,
    Manual,
}

/// <summary>Measured effect of one successful compaction transaction.</summary>
public sealed record CompactionReport(
    CompactionTrigger Trigger,
    int TokensBefore,
    int TokensAfter,
    ImmutableArray<CompactionStep> Steps);

/// <summary>
/// One mechanism applied inside a compaction transaction. A list is required
/// because microcompact may run first and full compact may still be necessary.
/// </summary>
public abstract record CompactionStep
{
    private CompactionStep() { }

    public sealed record Microcompact(
        int TokensBefore,
        int TokensAfter,
        ImmutableArray<string> ClearedToolCallIds) : CompactionStep;

    public sealed record FullCompact(
        int TokensBefore,
        int TokensAfter,
        int SummaryTokens,
        int PreservedTailMessages) : CompactionStep;
}

/// <summary>An expected reason that a requested compaction could not commit.</summary>
public sealed record CompactionFailure(
    CompactionFailureKind Kind,
    string Message);

public enum CompactionFailureKind
{
    ProviderError,
    InvalidSummary,
    NoCompactableHistory,
    StillOverLimit,
}
