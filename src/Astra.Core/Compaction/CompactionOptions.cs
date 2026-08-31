using Microsoft.Extensions.Options;

namespace Astra.Core.Compaction;

/// <summary>Token budget and retention policy for context compaction.</summary>
public sealed class CompactionOptions
{
    public const string SectionName = "Compaction";

    public bool Enabled { get; set; } = true;
    public int ContextWindowTokens { get; set; } = 200_000;
    public int MaxOutputTokens { get; set; }
    public int AutoCompactBufferTokens { get; set; } = 13_000;
    public int? AutoCompactThresholdOverrideTokens { get; set; }
    public int SummaryMaxOutputTokens { get; set; }
    public int FixedInputTokens { get; set; }
    public int KeepRecentToolResults { get; set; } = 5;
    public int MinimumMicrocompactSavingsTokens { get; set; } = 10_000;
    public TimeSpan MicrocompactColdAfter { get; set; } = TimeSpan.FromMinutes(60);
    public int PreserveRecentUserTurns { get; set; } = 1;
    public HashSet<string> CompactableToolNames { get; set; } = new(StringComparer.Ordinal);

    public int AutoCompactThresholdTokens =>
        AutoCompactThresholdOverrideTokens
        ?? ContextWindowTokens - Math.Min(MaxOutputTokens, 20_000) - AutoCompactBufferTokens;

    internal void Validate()
    {
        if (!Enabled)
            return;

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

public sealed class CompactionOptionsValidator : IValidateOptions<CompactionOptions>
{
    public ValidateOptionsResult Validate(string? name, CompactionOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
