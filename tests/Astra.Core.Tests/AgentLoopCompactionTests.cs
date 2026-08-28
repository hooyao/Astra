using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Astra.Core.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

public class AgentLoopCompactionTests
{
    private sealed class CountingCompactor : IContextCompactor
    {
        public List<List<ChatMessage>> Calls { get; } = [];
        public Func<int, IReadOnlyList<ChatMessage>, CompactionResult>? OnCall { get; init; }

        public ValueTask<CompactionResult> CompactIfNeededAsync(
            IReadOnlyList<ChatMessage> messages,
            CompactionTrigger trigger,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(messages.ToList());
            var result = OnCall?.Invoke(Calls.Count, messages)
                ?? new CompactionResult.NotNeeded(10, 100);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ToolThenTextClient : IChatClient
    {
        public int CallCount { get; private set; }
        public List<List<ChatMessage>> Calls { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            Calls.Add(messages.ToList());
            await Task.CompletedTask;

            if (CallCount == 1)
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "large_read", null)]);
            else
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class LargeReadTool : ITool
    {
        public string Name => "large_read";
        public string Description => "Return a large deterministic result.";
        public System.Text.Json.JsonElement InputSchema { get; } =
            System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Read;

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new ToolOutput.Result(new string('x', 4_000));
        }
    }

    [Fact]
    public async Task PreflightRunsBeforeEveryModelRoundTrip_IncludingAfterToolResult()
    {
        var model = new ToolThenTextClient();
        var compactor = new CountingCompactor();
        var loop = new AgentLoop(
            model,
            [new LargeReadTool()],
            contextCompactor: compactor);

        await foreach (var _ in loop.SubmitAsync("read it")) { }

        Assert.Equal(2, model.CallCount);
        Assert.Equal(2, compactor.Calls.Count);
        Assert.Empty(compactor.Calls[0]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.Single(compactor.Calls[1]
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task AppliedCandidate_IsInstalledBeforeModelCall_AndEmitsReport()
    {
        var model = new ToolThenTextClient();
        var candidate = ImmutableArray.Create(
            new ChatMessage(ChatRole.System, "stable-system"),
            new ChatMessage(ChatRole.User, "COMPACTED-SUMMARY"));
        var report = new CompactionReport(
            CompactionTrigger.Automatic,
            900,
            100,
            [new CompactionStep.FullCompact(900, 100, 50, 0)]);
        var compactor = new CountingCompactor
        {
            OnCall = (call, _) => call == 1
                ? new CompactionResult.Applied(candidate, report)
                : new CompactionResult.NotNeeded(100, 800),
        };
        var loop = new AgentLoop(model, [new LargeReadTool()], contextCompactor: compactor);

        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("original-user"))
            events.Add(evt);

        var compactEvent = Assert.IsType<AgentEvent.CompactionCompleted>(events[0]);
        Assert.Same(report, compactEvent.Report);
        Assert.Equal("COMPACTED-SUMMARY", model.Calls[0][^1].Text);
        Assert.DoesNotContain(model.Calls[0], message => message.Text == "original-user");
    }

    [Fact]
    public async Task FailedCompaction_StopsBeforeModelCall()
    {
        var model = new ToolThenTextClient();
        var compactor = new CountingCompactor
        {
            OnCall = (_, _) => new CompactionResult.Failed(
                new CompactionFailure(CompactionFailureKind.ProviderError, "offline"),
                900,
                800),
        };
        var loop = new AgentLoop(model, [], contextCompactor: compactor);

        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("hello"))
            events.Add(evt);

        Assert.Equal(0, model.CallCount);
        var error = Assert.IsType<AgentEvent.Error>(Assert.Single(events));
        Assert.Contains("ProviderError", error.Message);
    }
}
