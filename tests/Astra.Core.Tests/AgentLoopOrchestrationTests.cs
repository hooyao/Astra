using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D3 — orchestration at the loop level: the loop must actually run a read batch
/// concurrently, run a write alone, keep a write barrier from being crossed, and
/// map each result back to its CallId even when the batch completes out of order.
/// </summary>
public class AgentLoopOrchestrationTests
{
    /// <summary>A one-shot barrier: N participants each arrive and all release together.</summary>
    private sealed class Rendezvous(int expected)
    {
        private readonly TaskCompletionSource _allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public async Task ArriveAndWaitAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _count) >= expected)
                _allArrived.TrySetResult();
            await _allArrived.Task.WaitAsync(ct);
        }
    }

    // A read tool that proves concurrency by construction: on entry it signals a
    // shared rendezvous and waits until the whole batch has arrived. If the loop
    // ran these serially, the second would never start, the first would wait
    // forever, and the test would TIME OUT. Only genuine overlap lets all
    // participants reach the barrier and proceed.
    private sealed class RendezvousReadTool(string name, Rendezvous rv) : ITool
    {
        public string Name => name;
        public string Description => "read tool that rendezvous-syncs to prove overlap";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Read;

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await rv.ArriveAndWaitAsync(ct);
            yield return new ToolOutput.Result($"{name}-done");
        }
    }

    // A read tool that records its start/end window. No synchronization; safe when
    // a read is alone in its batch.
    private sealed class RecordingReadTool(string name, List<string> log) : ITool
    {
        public string Name => name;
        public string Description => "read tool that records its window";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Read;

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (log) log.Add($"{name}:start");
            await Task.Yield();
            lock (log) log.Add($"{name}:end");
            yield return new ToolOutput.Result($"{name}-read");
        }
    }

    // A write tool that records the wall-clock window it occupied, so we can assert
    // it did not overlap anything (it runs alone in a serial batch).
    private sealed class RecordingWriteTool(string name, List<string> log) : ITool
    {
        public string Name => name;
        public string Description => "write tool, runs alone";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Write;

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (log) log.Add($"{name}:start");
            await Task.Yield();
            lock (log) log.Add($"{name}:end");
            yield return new ToolOutput.Result($"{name}-wrote");
        }
    }

    private sealed class DelayedReadTool(string name, int delayMs) : ITool
    {
        public string Name => name;
        public string Description => "read tool with a fixed delay";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Read;

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            yield return new ToolOutput.Result($"{name}-done");
        }
    }

    // A scripted client that emits a fixed set of tool calls on the first turn,
    // then a final text answer once tool results come back.
    private sealed class MultiToolClient(IReadOnlyList<FunctionCallContent> firstTurnCalls) : IChatClient
    {
        public int CallCount { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.CompletedTask;

            if (messages.Last().Role == ChatRole.Tool)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
                yield break;
            }

            var contents = firstTurnCalls.Cast<AIContent>().ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, contents);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("streaming only");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static FunctionCallContent Call(string id, string toolName) =>
        new(id, toolName, new Dictionary<string, object?>());

    // ------------------------------------------------------------------
    // TWO reads in one turn must run concurrently. Proven by the rendezvous: the
    // tools only complete if both are in flight at once. A 5s timeout turns an
    // accidental serialization into a failure instead of a hang.
    // ------------------------------------------------------------------
    [Fact]
    public async Task TwoReads_RunConcurrently()
    {
        var rv = new Rendezvous(expected: 2);
        var tools = new ITool[]
        {
            new RendezvousReadTool("read_a", rv),
            new RendezvousReadTool("read_b", rv),
        };
        var model = new MultiToolClient([Call("1", "read_a"), Call("2", "read_b")]);
        var loop = new AgentLoop(model, tools);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        var results = events.OfType<AgentEvent.ToolResult>().Select(r => r.Result).ToHashSet();
        Assert.Contains("read_a-done", results);
        Assert.Contains("read_b-done", results);
        Assert.Equal(2, model.CallCount); // tool turn + final text turn
        Assert.IsType<AgentEvent.TextDelta>(events[^1]);
    }

    // ------------------------------------------------------------------
    // [read, write, read]: the write barrier must split the two reads. The write
    // runs alone, so its start/end bracket nothing else. We assert the write's
    // window is contiguous in the log (no other tool event between start and end).
    // ------------------------------------------------------------------
    [Fact]
    public async Task ReadWriteRead_WriteRunsAlone_BarrierNotCrossed()
    {
        var log = new List<string>();
        var tools = new ITool[]
        {
            new RecordingReadTool("read_a", log),
            new RecordingWriteTool("write_b", log),
            new RecordingReadTool("read_c", log),
        };
        var model = new MultiToolClient(
            [Call("1", "read_a"), Call("2", "write_b"), Call("3", "read_c")]);
        var loop = new AgentLoop(model, tools);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in loop.SubmitAsync("go", cts.Token)) { }

        // The write's window is contiguous: nothing logged between its start and end.
        var startIdx = log.IndexOf("write_b:start");
        var endIdx = log.IndexOf("write_b:end");
        Assert.True(startIdx >= 0 && endIdx == startIdx + 1,
            $"write batch was not exclusive; log = [{string.Join(", ", log)}]");
    }

    // ------------------------------------------------------------------
    // Results map back to the RIGHT CallId even when tools finish out of order.
    // read_slow finishes after read_fast, but each result must carry its own id.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Results_MapToCallId_EvenWhenOutOfOrder()
    {
        var tools = new ITool[]
        {
            new DelayedReadTool("read_slow", delayMs: 50),
            new DelayedReadTool("read_fast", delayMs: 0),
        };
        var model = new MultiToolClient([Call("slow-id", "read_slow"), Call("fast-id", "read_fast")]);
        var loop = new AgentLoop(model, tools);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        var byId = events.OfType<AgentEvent.ToolResult>().ToDictionary(r => r.CallId, r => r.Result);
        Assert.Equal("read_slow-done", byId["slow-id"]);
        Assert.Equal("read_fast-done", byId["fast-id"]);
    }
}
