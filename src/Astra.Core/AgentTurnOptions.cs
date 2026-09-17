namespace Astra.Core;

/// <summary>Per-turn limits and capability boundaries that do not belong to persistent AgentLoop state.</summary>
public sealed record AgentTurnOptions
{
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Allow only calls classified as <see cref="ToolAction.Read"/> for this turn.
    /// The definitions remain visible so one stable worker loop can serve both
    /// read-only and write-capable requests; enforcement still occurs before
    /// executor activation.
    /// </summary>
    public bool ReadOnlyTools { get; init; }

    internal void Validate()
    {
        if (MaxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
    }
}
