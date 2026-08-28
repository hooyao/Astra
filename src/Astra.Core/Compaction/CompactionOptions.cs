using System.Collections.Immutable;

namespace Astra.Core.Compaction;

/// <summary>Token budget and retention policy for context compaction.</summary>
public sealed record CompactionOptions
{
    public int ContextWindowTokens { get; init; } = 200_000;
    public int MaxOutputTokens { get; init; } = 20_000;
    public int AutoCompactBufferTokens { get; init; } = 13_000;
    public int? AutoCompactThresholdOverrideTokens { get; init; }
    public int SummaryMaxOutputTokens { get; init; } = 20_000;
    public int FixedInputTokens { get; init; }
    public int KeepRecentToolResults { get; init; } = 5;
    public int MinimumMicrocompactSavingsTokens { get; init; } = 10_000;
    public TimeSpan MicrocompactColdAfter { get; init; } = TimeSpan.FromMinutes(60);
    public int PreserveRecentUserTurns { get; init; } = 1;
    public ImmutableHashSet<string> CompactableToolNames { get; init; } =
        ImmutableHashSet.Create<string>(StringComparer.Ordinal);

    public int AutoCompactThresholdTokens =>
        AutoCompactThresholdOverrideTokens
        ?? ContextWindowTokens - Math.Min(MaxOutputTokens, 20_000) - AutoCompactBufferTokens;

    internal void Validate()
    {
        if (ContextWindowTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(ContextWindowTokens));
        if (MaxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        if (AutoCompactBufferTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(AutoCompactBufferTokens));
        if (AutoCompactThresholdOverrideTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(AutoCompactThresholdOverrideTokens));
        if (SummaryMaxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(SummaryMaxOutputTokens));
        if (SummaryMaxOutputTokens > Math.Min(MaxOutputTokens, 20_000))
            throw new ArgumentException(
                "SummaryMaxOutputTokens cannot exceed the output reserve used by the threshold.",
                nameof(SummaryMaxOutputTokens));
        if (FixedInputTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(FixedInputTokens));
        if (KeepRecentToolResults < 0)
            throw new ArgumentOutOfRangeException(nameof(KeepRecentToolResults));
        if (MinimumMicrocompactSavingsTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(MinimumMicrocompactSavingsTokens));
        if (MicrocompactColdAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MicrocompactColdAfter));
        if (PreserveRecentUserTurns < 0)
            throw new ArgumentOutOfRangeException(nameof(PreserveRecentUserTurns));
        if (CompactableToolNames.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Compactable tool names cannot be blank.", nameof(CompactableToolNames));
        if (AutoCompactThresholdTokens >= ContextWindowTokens)
            throw new ArgumentException("The auto-compact threshold must be below the context window.");
        if (AutoCompactThresholdTokens <= 0)
            throw new ArgumentException("The configured reserves leave no usable auto-compact threshold.");
    }
}
