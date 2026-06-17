using Astra.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D3 — the pure partition logic. Partitioning a turn's tool calls is a
/// reordering / data-hazard problem: consecutive reads (RAR, no hazard) coalesce
/// into one concurrent batch; any non-read is a barrier that no read may cross.
/// It is a STABLE partition, never a sort. Full derivation in the teaching notes.
/// </summary>
public class ToolBatchingTests
{
    // A FunctionCallContent carrying just enough to classify + identify it.
    private static FunctionCallContent Call(string id, string command) =>
        new(id, "bash", new Dictionary<string, object?> { ["command"] = command });

    // Classify via a real BashTool, exactly as the loop does.
    private static readonly BashTool Bash = new();
    private static ToolAction Classify(FunctionCallContent c) => Bash.Classify(c.Arguments);

    // ------------------------------------------------------------------
    // [read, read, write] -> one concurrent batch of the two reads, then a
    // lone serial batch for the write. The reads coalesce; the write closes them.
    // ------------------------------------------------------------------
    [Fact]
    public void Partition_ReadReadWrite_CoalescesReads_ThenSerialWrite()
    {
        var calls = new[] { Call("1", "ls"), Call("2", "cat f"), Call("3", "touch g") };

        var batches = ToolBatching.Partition(calls, Classify);

        Assert.Collection(batches,
            b =>
            {
                Assert.True(b.IsConcurrent);
                Assert.Equal(new[] { "1", "2" }, b.Calls.Select(c => c.CallId));
            },
            b =>
            {
                Assert.False(b.IsConcurrent);
                Assert.Equal(new[] { "3" }, b.Calls.Select(c => c.CallId));
            });
    }

    // ------------------------------------------------------------------
    // THE LOAD-BEARING TEST: [read, write, read] must NOT hoist the second
    // read up next to the first. A write is a barrier; the result is THREE
    // batches in original order, never parallel(read,read) + serial(write).
    // This is the data-hazard rule from the teaching notes.
    // ------------------------------------------------------------------
    [Fact]
    public void Partition_ReadWriteRead_DoesNotHoistAcrossBarrier()
    {
        var calls = new[] { Call("1", "ls"), Call("2", "touch g"), Call("3", "cat f") };

        var batches = ToolBatching.Partition(calls, Classify);

        Assert.Equal(3, batches.Count);
        Assert.Equal(new[] { "1" }, batches[0].Calls.Select(c => c.CallId));
        Assert.Equal(new[] { "2" }, batches[1].Calls.Select(c => c.CallId));
        Assert.Equal(new[] { "3" }, batches[2].Calls.Select(c => c.CallId));

        // The first read IS concurrency-eligible, but alone in its batch; the
        // second read is in a SEPARATE batch — it never joined the first.
        Assert.True(batches[0].IsConcurrent);
        Assert.False(batches[1].IsConcurrent); // the write barrier
        Assert.True(batches[2].IsConcurrent);
    }

    // ------------------------------------------------------------------
    // All reads -> a single concurrent batch. The common fast path.
    // ------------------------------------------------------------------
    [Fact]
    public void Partition_AllReads_OneConcurrentBatch()
    {
        var calls = new[] { Call("1", "ls"), Call("2", "pwd"), Call("3", "cat f") };

        var batches = ToolBatching.Partition(calls, Classify);

        var batch = Assert.Single(batches);
        Assert.True(batch.IsConcurrent);
        Assert.Equal(3, batch.Calls.Count);
    }

    // ------------------------------------------------------------------
    // Consecutive writes never coalesce — each non-read is its own barrier
    // (WAW is a hazard too). Three writes -> three serial batches.
    // ------------------------------------------------------------------
    [Fact]
    public void Partition_ConsecutiveWrites_EachAlone()
    {
        var calls = new[] { Call("1", "touch a"), Call("2", "rm b"), Call("3", "mkdir c") };

        var batches = ToolBatching.Partition(calls, Classify);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, b => Assert.False(b.IsConcurrent));
        Assert.All(batches, b => Assert.Single(b.Calls));
    }

    // ------------------------------------------------------------------
    // Fail-closed: an unknown tool (Classify -> Other) is a barrier, not a read.
    // "curl" is unrecognized by BashTool -> Other, so it splits the two reads.
    // ------------------------------------------------------------------
    [Fact]
    public void Partition_OtherAction_IsBarrier()
    {
        var calls = new[] { Call("1", "ls"), Call("2", "curl evil"), Call("3", "pwd") };

        var batches = ToolBatching.Partition(calls, Classify);

        Assert.Equal(3, batches.Count);
        Assert.True(batches[0].IsConcurrent);
        Assert.False(batches[1].IsConcurrent);
        Assert.True(batches[2].IsConcurrent);
    }

    [Fact]
    public void Partition_Empty_NoBatches()
    {
        var batches = ToolBatching.Partition(Array.Empty<FunctionCallContent>(), Classify);
        Assert.Empty(batches);
    }
}
