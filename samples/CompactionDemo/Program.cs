using System.Runtime.CompilerServices;
using System.Collections.Immutable;
using Astra.Core.Compaction;
using Astra.Providers;
using Microsoft.Extensions.AI;

const string retentionCode = "RETENTION-CODE-7429";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== D7 Compaction Demo ===\n");

await RunDeterministicMicrocompactAsync();
await RunDeterministicFullCompactAsync();

if (args.Contains("--real", StringComparer.Ordinal))
    await RunRealFullCompactAsync();
else
    Console.WriteLine("PART 3 skipped. Add --real to use the configured local gpt-5.6 endpoint.\n");

static async Task RunDeterministicMicrocompactAsync()
{
    Console.WriteLine("PART 1: deterministic microcompact\n");

    var estimator = new RoughChatTokenEstimator();
    var messages = BuildToolHistory(payloadCharacters: 9_000);
    var compactor = new ContextCompactor(
        new FixedSummaryClient("must not be called"),
        estimator,
        new CompactionOptions
        {
            ContextWindowTokens = 100_000,
            MaxOutputTokens = 1_000,
            AutoCompactBufferTokens = 1_000,
            SummaryMaxOutputTokens = 1_000,
            AutoCompactThresholdOverrideTokens = 11_000,
            KeepRecentToolResults = 2,
            MinimumMicrocompactSavingsTokens = 1,
            CompactableToolNames = ImmutableHashSet.Create(
                StringComparer.Ordinal, "read", "grep", "bash"),
        });

    PrintToolResults("before", messages);
    var result = await compactor.CompactIfNeededAsync(
        messages, CompactionTrigger.Automatic, CancellationToken.None);
    var applied = RequireApplied(result);
    PrintToolResults("after ", applied.CandidateMessages);
    PrintReport(applied.Report);

    var originalStillIntact = ToolResults(messages)
        .All(result => !Equals(result.Result, ContextCompactor.ClearedToolResultText));
    Console.WriteLine($"  original history unchanged: {originalStillIntact}\n");
}

static async Task RunDeterministicFullCompactAsync()
{
    Console.WriteLine("PART 2: deterministic full compact\n");

    var messages = BuildLongConversation();
    var estimator = new RoughChatTokenEstimator();
    var before = estimator.EstimateTokens(messages);
    var summary = $"Decision retained verbatim: {retentionCode}. Re-readable bulk stays at docs/input.txt.";
    var compactor = new ContextCompactor(
        new FixedSummaryClient(summary),
        estimator,
        OptionsThatTriggerAt(before));

    var result = await compactor.CompactIfNeededAsync(
        messages, CompactionTrigger.Automatic, CancellationToken.None);
    var applied = RequireApplied(result);

    PrintReport(applied.Report);
    Console.WriteLine($"  old bulk still active: {ContainsText(applied.CandidateMessages, "RE-READABLE-BULK")}");
    Console.WriteLine($"  retention code active: {ContainsText(applied.CandidateMessages, retentionCode)}");
    Console.WriteLine($"  recent tail verbatim: {applied.CandidateMessages[^1].Text}\n");
}

static async Task RunRealFullCompactAsync()
{
    Console.WriteLine("PART 3: real gpt-5.6 full compact and continuation\n");

    var endpoint = Environment.GetEnvironmentVariable("ASTRA_LLM_ENDPOINT")
        ?? "http://localhost:8765/codex";
    var model = Environment.GetEnvironmentVariable("ASTRA_LLM_MODEL")
        ?? "gpt-5.6-sol";
    var apiKey = Environment.GetEnvironmentVariable("ASTRA_LLM_API_KEY")
        ?? string.Empty;

    using var client = ChatClientFactory.Create(new LlmConfig
    {
        Provider = "OpenAIResponses",
        Endpoint = endpoint,
        DeploymentName = model,
        ApiKey = apiKey,
        MaxOutputTokens = 1_000,
    });

    var messages = BuildLongConversation();
    var estimator = new RoughChatTokenEstimator();
    var before = estimator.EstimateTokens(messages);
    var compactor = new ContextCompactor(client, estimator, OptionsThatTriggerAt(before));

    var result = await compactor.CompactIfNeededAsync(
        messages, CompactionTrigger.Automatic, CancellationToken.None);
    var applied = RequireApplied(result);
    PrintReport(applied.Report);

    var summaryText = applied.CandidateMessages
        .First(message => message.Text?.Contains("<summary>", StringComparison.Ordinal) == true)
        .Text;
    Console.WriteLine("  generated summary:");
    foreach (var line in (summaryText ?? string.Empty).Split('\n'))
        Console.WriteLine($"    | {line}");

    var continuation = await client.GetResponseAsync(
        applied.CandidateMessages,
        new ChatOptions { MaxOutputTokens = 200 });
    Console.WriteLine($"\n  continuation: {continuation.Text}");
    Console.WriteLine($"  retained exact code: {continuation.Text.Contains(retentionCode, StringComparison.Ordinal)}\n");
}

static CompactionOptions OptionsThatTriggerAt(int tokensBefore) => new()
{
    ContextWindowTokens = tokensBefore + 512 + 64 - 1,
    MaxOutputTokens = 512,
    AutoCompactBufferTokens = 64,
    SummaryMaxOutputTokens = 512,
    KeepRecentToolResults = 5,
    MinimumMicrocompactSavingsTokens = 10_000,
    PreserveRecentUserTurns = 1,
};

static List<ChatMessage> BuildLongConversation() =>
[
    new(ChatRole.System, "You are Astra. Preserve exact identifiers required by later work."),
    new(
        ChatRole.User,
        $"The exact unresolved retention code is {retentionCode}. It must survive compaction. " +
        "The following file content is reproducible at docs/input.txt:\n" +
        "RE-READABLE-BULK:" + new string('x', 18_000)),
    new(
        ChatRole.Assistant,
        $"Confirmed. The future task depends on exact code {retentionCode}; the file bulk is re-readable."),
    new(
        ChatRole.User,
        "Continue after compaction. State the exact retention code and why it was retained, in one sentence."),
];

static List<ChatMessage> BuildToolHistory(int payloadCharacters) =>
[
    new(ChatRole.Assistant, [new FunctionCallContent("call-1", "read", null)]),
    new(ChatRole.Tool, [new FunctionResultContent("call-1", "old-1:" + new string('a', payloadCharacters))]),
    new(ChatRole.Assistant, [new FunctionCallContent("call-2", "grep", null)]),
    new(ChatRole.Tool, [new FunctionResultContent("call-2", "old-2:" + new string('b', payloadCharacters))]),
    new(ChatRole.Assistant, [new FunctionCallContent("call-3", "read", null)]),
    new(ChatRole.Tool, [new FunctionResultContent("call-3", "recent-3:" + new string('c', payloadCharacters))]),
    new(ChatRole.Assistant, [new FunctionCallContent("call-4", "bash", null)]),
    new(ChatRole.Tool, [new FunctionResultContent("call-4", "recent-4:" + new string('d', payloadCharacters))]),
];

static List<FunctionResultContent> ToolResults(IEnumerable<ChatMessage> messages) =>
    messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().ToList();

static void PrintToolResults(string label, IEnumerable<ChatMessage> messages)
{
    Console.WriteLine($"  {label}:");
    foreach (var result in ToolResults(messages))
    {
        var text = result.Result?.ToString() ?? string.Empty;
        var state = text == ContextCompactor.ClearedToolResultText
            ? "CLEARED"
            : $"{text.Length:N0} chars";
        Console.WriteLine($"    {result.CallId}: {state}");
    }
}

static void PrintReport(CompactionReport report)
{
    Console.WriteLine($"  tokens: {report.TokensBefore:N0} -> {report.TokensAfter:N0}");
    foreach (var step in report.Steps)
    {
        switch (step)
        {
            case CompactionStep.Microcompact micro:
                Console.WriteLine(
                    $"  microcompact: cleared [{string.Join(", ", micro.ClearedToolCallIds)}], " +
                    $"{micro.TokensBefore:N0} -> {micro.TokensAfter:N0}");
                break;
            case CompactionStep.FullCompact full:
                Console.WriteLine(
                    $"  full compact: summary={full.SummaryTokens:N0} tokens, " +
                    $"tail={full.PreservedTailMessages} messages, " +
                    $"{full.TokensBefore:N0} -> {full.TokensAfter:N0}");
                break;
        }
    }
}

static bool ContainsText(IEnumerable<ChatMessage> messages, string value) =>
    messages.Any(message => message.Text?.Contains(value, StringComparison.Ordinal) == true);

static CompactionResult.Applied RequireApplied(CompactionResult result) => result switch
{
    CompactionResult.Applied applied => applied,
    CompactionResult.NotNeeded notNeeded => throw new InvalidOperationException(
        $"Demo did not trigger: {notNeeded.InputTokens} < {notNeeded.ThresholdTokens}."),
    CompactionResult.Failed failed => throw new InvalidOperationException(
        $"Demo compaction failed ({failed.Failure.Kind}): {failed.Failure.Message}"),
    _ => throw new InvalidOperationException("Unknown compaction result."),
};

internal sealed class FixedSummaryClient(string summary) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary)));

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
