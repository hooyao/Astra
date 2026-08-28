using Microsoft.Extensions.AI;

namespace Astra.Core.Compaction;

/// <summary>
/// Estimates provider input tokens for a message list. Exact tokenization is
/// model-specific, so callers must treat this value as an estimate unless the
/// implementation explicitly uses provider-reported usage.
/// </summary>
public interface IChatTokenEstimator
{
    int EstimateTokens(IReadOnlyList<ChatMessage> messages);
}
