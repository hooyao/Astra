using Microsoft.Extensions.AI;

namespace Astra.Core;

/// <summary>
/// One batch of tool calls from a single turn, after partitioning. Either a run
/// of consecutive read-only calls that may execute concurrently
/// (<see cref="IsConcurrent"/> == true), or a single non-read call that must run
/// alone, serially.
/// </summary>
/// <param name="IsConcurrent">True if every call in <see cref="Calls"/> is read-only
/// and the batch may run in parallel.</param>
/// <param name="Calls">The calls in this batch, in the model's original emission order.</param>
public sealed record ToolBatch(bool IsConcurrent, IReadOnlyList<FunctionCallContent> Calls);

/// <summary>
/// Partitions a turn's tool calls into concurrent and serial batches. This is the
/// one piece of real logic in tool orchestration, and it is a *reordering* problem
/// in disguise — identical to instruction scheduling in a compiler/CPU. The full
/// derivation lives in <c>agent/experiments/d03-tool-orchestration/teaching-notes.md</c>;
/// the load-bearing rule is summarized here because it is easy to get wrong.
///
/// Treat a tool's read as a memory load and its write as a store. Reordering two
/// operations is legal only when they carry no data hazard:
///
///   - RAR (read-after-read):   no hazard  → reads may run in any order / parallel.
///   - RAW / WAR / WAW:         hazard     → a read may NOT cross a write, and
///                                            writes may not cross each other.
///
/// We cannot prove two tool calls don't touch the same file (there is no alias
/// analysis over the filesystem/world), so every non-read call is a *barrier*: no
/// read may be hoisted across it. The model's emission order is the contract — if
/// it wanted call C to observe write B's effect it emitted C after B; the harness
/// must not reorder them. So this is a **stable partition** (coalesce adjacent
/// reads), never a sort (which would hoist reads across barriers and change
/// semantics).
/// </summary>
public static class ToolBatching
{
    /// <summary>
    /// Fold the calls (in order) into batches. A read coalesces into the currently
    /// open concurrent batch; any non-read closes that batch and emits itself as a
    /// lone serial batch. Order between batches is never changed.
    /// </summary>
    /// <param name="calls">The turn's tool calls, in the model's emission order.</param>
    /// <param name="classify">Maps a call to its <see cref="ToolAction"/>. A call is
    /// concurrency-safe iff it classifies as <see cref="ToolAction.Read"/>; every
    /// other action (Write/Execute/Other, including unknown tools) is a barrier.
    /// This is the single point where "concurrency-safe" is derived from D2's
    /// behavioral classification — there is no separate concurrency flag.</param>
    public static List<ToolBatch> Partition(
        IReadOnlyList<FunctionCallContent> calls,
        Func<FunctionCallContent, ToolAction> classify)
    {
        var batches = new List<ToolBatch>();

        // The currently-open concurrent batch, or null if the last batch was a
        // barrier. We mutate this list after wrapping it in a ToolBatch; the batch
        // exposes it only as IReadOnlyList, so the mutation is contained here.
        List<FunctionCallContent>? openReadBatch = null;

        foreach (var call in calls)
        {
            if (classify(call) == ToolAction.Read)
            {
                // RAR-safe: join the open concurrent batch, or start one.
                if (openReadBatch is null)
                {
                    openReadBatch = [];
                    batches.Add(new ToolBatch(IsConcurrent: true, openReadBatch));
                }
                openReadBatch.Add(call);
            }
            else
            {
                // Barrier (write/execute/other): close any open read run so no
                // later read can coalesce across this call, then emit it alone.
                openReadBatch = null;
                batches.Add(new ToolBatch(IsConcurrent: false, [call]));
            }
        }

        return batches;
    }
}
