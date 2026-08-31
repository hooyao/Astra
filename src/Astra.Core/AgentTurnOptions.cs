namespace Astra.Core;

/// <summary>Per-turn model limits that do not belong to persistent AgentLoop state.</summary>
public sealed record AgentTurnOptions
{
    public int? MaxOutputTokens { get; init; }

    internal void Validate()
    {
        if (MaxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
    }
}
