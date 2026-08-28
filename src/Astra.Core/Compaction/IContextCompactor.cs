using Microsoft.Extensions.AI;

namespace Astra.Core.Compaction;

/// <summary>
/// Applies context-pressure policy without mutating caller-owned messages.
/// Cancellation propagates; expected operational failures are returned as
/// <see cref="CompactionResult.Failed"/>.
/// </summary>
public interface IContextCompactor
{
    ValueTask<CompactionResult> CompactIfNeededAsync(
        IReadOnlyList<ChatMessage> messages,
        CompactionTrigger trigger,
        CancellationToken ct);
}
