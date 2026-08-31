using System.Runtime.CompilerServices;
using Astra.Core.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Astra.Core.Tests;

public class ContextCompactorTests
{
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class StubSummaryClient(
        string summary = "summary",
        Exception? failure = null) : IChatClient
    {
        public int CallCount { get; private set; }
        public List<List<ChatMessage>> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Calls.Add(messages.ToList());
            if (failure is not null)
                return Task.FromException<ChatResponse>(failure);

            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, summary)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            throw new NotSupportedException();
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ThrowingTokenEstimator : IChatTokenEstimator
    {
        public int EstimateTokens(IReadOnlyList<ChatMessage> messages) =>
            throw new InvalidOperationException("must not estimate");
    }

    [Fact]
    public async Task DisabledConfiguration_ReturnsNotNeededWithoutEstimatingOrCallingModel()
    {
        var summaryClient = new StubSummaryClient(failure: new InvalidOperationException("must not run"));
        var compactor = new ContextCompactor(
            summaryClient,
            new ThrowingTokenEstimator(),
            Options.Create(new CompactionOptions { Enabled = false }),
            TimeProvider.System);

        var result = await compactor.CompactIfNeededAsync(
            [new ChatMessage(ChatRole.User, new string('x', 10_000))],
            CompactionTrigger.Automatic,
            CancellationToken.None);

        Assert.IsType<CompactionResult.NotNeeded>(result);
        Assert.Equal(0, summaryClient.CallCount);
    }

    [Fact]
    public async Task BelowThreshold_WithNoEligibleOldResults_ReturnsNotNeeded()
    {
        var summaryClient = new StubSummaryClient();
        var compactor = CreateCompactor(summaryClient, contextWindow: 10_000);
        ChatMessage[] messages =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "short question"),
        ];

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        var notNeeded = Assert.IsType<CompactionResult.NotNeeded>(result);
        Assert.True(notNeeded.InputTokens < notNeeded.ThresholdTokens);
        Assert.Equal(0, summaryClient.CallCount);
    }

    [Fact]
    public async Task Microcompact_ClearsOldPayloads_ButPreservesIdsAndRecentResult()
    {
        var summaryClient = new StubSummaryClient(failure: new InvalidOperationException("must not run"));
        var compactor = CreateCompactor(
            summaryClient,
            contextWindow: 100_000,
            keepRecentToolResults: 1,
            minimumMicrocompactSavingsTokens: 100,
            thresholdOverride: 600);
        var messages = ThreeToolResults(payloadCharacters: 900);

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        var applied = Assert.IsType<CompactionResult.Applied>(result);
        var step = Assert.IsType<CompactionStep.Microcompact>(Assert.Single(applied.Report.Steps));
        Assert.Equal(["call-1", "call-2"], step.ClearedToolCallIds.ToArray());
        Assert.True(step.TokensAfter < step.TokensBefore);
        Assert.Equal(0, summaryClient.CallCount);

        var originalResults = Results(messages);
        Assert.All(originalResults, item => Assert.StartsWith("payload-", Assert.IsType<string>(item.Result)));

        var compactedResults = Results(applied.CandidateMessages);
        Assert.Equal(ContextCompactor.ClearedToolResultText, compactedResults[0].Result);
        Assert.Equal(ContextCompactor.ClearedToolResultText, compactedResults[1].Result);
        Assert.StartsWith("payload-3", Assert.IsType<string>(compactedResults[2].Result));
        Assert.Equal(["call-1", "call-2", "call-3"], compactedResults.Select(result => result.CallId).ToArray());
    }

    [Fact]
    public async Task Microcompact_PreservesResultsFromToolsOutsideAllowlist()
    {
        var summaryClient = new StubSummaryClient(failure: new InvalidOperationException("must not run"));
        var compactor = CreateCompactor(
            summaryClient,
            contextWindow: 100_000,
            keepRecentToolResults: 0,
            minimumMicrocompactSavingsTokens: 1);
        ChatMessage[] messages =
        [
            new(ChatRole.Assistant, [new FunctionCallContent("agent-1", "agent", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("agent-1", new string('x', 9_000))]),
        ];

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        Assert.IsType<CompactionResult.NotNeeded>(result);
        Assert.Equal(new string('x', 9_000), Assert.IsType<string>(Results(messages)[0].Result));
        Assert.Equal(0, summaryClient.CallCount);
    }

    [Fact]
    public async Task Microcompact_DoesNotRewriteWarmContextBelowPressure()
    {
        var summaryClient = new StubSummaryClient(failure: new InvalidOperationException("must not run"));
        var compactor = CreateCompactor(
            summaryClient,
            contextWindow: 100_000,
            keepRecentToolResults: 1,
            minimumMicrocompactSavingsTokens: 1);
        var messages = ThreeToolResults(payloadCharacters: 9_000);

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        Assert.IsType<CompactionResult.NotNeeded>(result);
        Assert.All(Results(messages), item => Assert.StartsWith("payload-", Assert.IsType<string>(item.Result)));
        Assert.Equal(0, summaryClient.CallCount);
    }

    [Fact]
    public async Task Microcompact_RewritesColdContextBelowPressure()
    {
        var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var messages = ThreeToolResults(payloadCharacters: 900);
        messages.Last(message => message.Role == ChatRole.Assistant).CreatedAt = now.AddMinutes(-61);
        var compactor = new ContextCompactor(
            new StubSummaryClient(failure: new InvalidOperationException("must not run")),
            new RoughChatTokenEstimator(),
            Options.Create(new CompactionOptions
            {
                ContextWindowTokens = 100_000,
                MaxOutputTokens = 100,
                AutoCompactBufferTokens = 100,
                SummaryMaxOutputTokens = 100,
                KeepRecentToolResults = 1,
                MinimumMicrocompactSavingsTokens = 100,
                CompactableToolNames = ["read"],
            }),
            new FixedTimeProvider(now));

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        var applied = Assert.IsType<CompactionResult.Applied>(result);
        Assert.IsType<CompactionStep.Microcompact>(Assert.Single(applied.Report.Steps));
    }

    [Fact]
    public async Task FullCompact_SummarizesOldTurns_AndPreservesCurrentTurnVerbatim()
    {
        var summaryClient = new StubSummaryClient("<summary>keep decision A</summary>");
        var compactor = CreateCompactor(summaryClient, contextWindow: 1_000);
        ChatMessage[] messages =
        [
            new(ChatRole.System, "stable-system-prefix"),
            new(ChatRole.User, "OLD-QUESTION-" + new string('x', 2_000)),
            new(ChatRole.Assistant, "OLD-ANSWER-" + new string('y', 2_000)),
            new(ChatRole.User, "CURRENT-TAIL"),
        ];

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        var applied = Assert.IsType<CompactionResult.Applied>(result);
        var step = Assert.IsType<CompactionStep.FullCompact>(Assert.Single(applied.Report.Steps));
        Assert.Equal(1, step.PreservedTailMessages);
        Assert.True(applied.Report.TokensAfter < applied.Report.TokensBefore);

        Assert.Equal(1, summaryClient.CallCount);
        var summaryInputText = string.Join("\n", summaryClient.Calls[0].Select(message => message.Text));
        Assert.Contains("OLD-QUESTION", summaryInputText);
        Assert.DoesNotContain("CURRENT-TAIL", summaryInputText);

        Assert.Equal("stable-system-prefix", applied.CandidateMessages[0].Text);
        Assert.Contains("keep decision A", applied.CandidateMessages[1].Text);
        Assert.Equal("CURRENT-TAIL", applied.CandidateMessages[^1].Text);
    }

    [Fact]
    public async Task PressurePipeline_RecordsMicrocompactThenFullCompact_InOrder()
    {
        var summaryClient = new StubSummaryClient("retained decision");
        var compactor = CreateCompactor(
            summaryClient,
            contextWindow: 100_000,
            keepRecentToolResults: 1,
            minimumMicrocompactSavingsTokens: 1,
            thresholdOverride: 500);
        ChatMessage[] messages =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "OLD-" + new string('u', 3_000)),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", new string('a', 900))]),
            new(ChatRole.Assistant, [new FunctionCallContent("call-2", "read", null)]),
            new(ChatRole.Tool, [new FunctionResultContent("call-2", new string('b', 900))]),
            new(ChatRole.Assistant, "old turn complete"),
            new(ChatRole.User, "CURRENT"),
        ];

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        var applied = Assert.IsType<CompactionResult.Applied>(result);
        Assert.Collection(
            applied.Report.Steps,
            step => Assert.IsType<CompactionStep.Microcompact>(step),
            step => Assert.IsType<CompactionStep.FullCompact>(step));
        Assert.Equal("CURRENT", applied.CandidateMessages[^1].Text);
        Assert.Equal(1, summaryClient.CallCount);
    }

    [Fact]
    public async Task FullCompact_ProviderFailure_ReturnsFailed_AndLeavesOriginalUntouched()
    {
        var summaryClient = new StubSummaryClient(failure: new InvalidOperationException("provider unavailable"));
        var compactor = CreateCompactor(summaryClient, contextWindow: 1_000);
        ChatMessage[] messages =
        [
            new(ChatRole.System, "system"),
            new(ChatRole.User, "OLD-" + new string('x', 3_000)),
            new(ChatRole.Assistant, "old answer"),
            new(ChatRole.User, "CURRENT"),
        ];
        var originalTexts = messages.Select(message => message.Text).ToArray();

        var result = await compactor.CompactIfNeededAsync(
            messages, CompactionTrigger.Automatic, CancellationToken.None);

        var failed = Assert.IsType<CompactionResult.Failed>(result);
        Assert.Equal(CompactionFailureKind.ProviderError, failed.Failure.Kind);
        Assert.Equal(originalTexts, messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Cancellation_Propagates_InsteadOfBecomingFailed()
    {
        var compactor = CreateCompactor(new StubSummaryClient(), contextWindow: 1_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await compactor.CompactIfNeededAsync(
                [new ChatMessage(ChatRole.User, new string('x', 3_000))],
                CompactionTrigger.Automatic,
                cts.Token));
    }

    private static ContextCompactor CreateCompactor(
        IChatClient summaryClient,
        int contextWindow,
        int keepRecentToolResults = 5,
        int minimumMicrocompactSavingsTokens = 10_000,
        int? thresholdOverride = null) =>
        new(
            summaryClient,
            new RoughChatTokenEstimator(),
            Options.Create(new CompactionOptions
            {
                ContextWindowTokens = contextWindow,
                MaxOutputTokens = 100,
                AutoCompactBufferTokens = 100,
                SummaryMaxOutputTokens = 100,
                KeepRecentToolResults = keepRecentToolResults,
                MinimumMicrocompactSavingsTokens = minimumMicrocompactSavingsTokens,
                PreserveRecentUserTurns = 1,
                CompactableToolNames = ["read"],
                AutoCompactThresholdOverrideTokens = thresholdOverride,
            }),
            TimeProvider.System);

    private static ChatMessage[] ThreeToolResults(int payloadCharacters) =>
    [
        new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read", null)]),
        new(ChatRole.Tool, [new FunctionResultContent("call-1", "payload-1" + new string('a', payloadCharacters))]),
        new(ChatRole.Assistant, [new FunctionCallContent("call-2", "read", null)]),
        new(ChatRole.Tool, [new FunctionResultContent("call-2", "payload-2" + new string('b', payloadCharacters))]),
        new(ChatRole.Assistant, [new FunctionCallContent("call-3", "read", null)]),
        new(ChatRole.Tool, [new FunctionResultContent("call-3", "payload-3" + new string('c', payloadCharacters))]),
    ];

    private static List<FunctionResultContent> Results(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();
}
