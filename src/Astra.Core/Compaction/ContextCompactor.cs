using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Astra.Core.Compaction;

/// <summary>
/// Provider-neutral D7 compaction pipeline: clear old tool-result payloads,
/// recount, then use a one-turn LLM summarizer only when still above threshold.
/// All work is detached from the caller's live history.
/// </summary>
public sealed class ContextCompactor : IContextCompactor
{
    public const string ClearedToolResultText = "[Old tool result content cleared]";

    private const string SummaryInstruction = """
        Create a continuation summary of the conversation supplied before this message.
        Respond with text only and do not call tools.

        Maximize recall of information required to continue the work, then improve
        precision by removing irrelevant or re-fetchable detail. Preserve:
        1. the user's requests and constraints;
        2. technical and architectural decisions with their reasons;
        3. files, symbols, commands, and code changes needed for continuation;
        4. unresolved errors verbatim when exact text matters;
        5. completed verification, pending tasks, and the exact next step.

        For large file or tool output that can be read again, retain only its durable
        identifier or path and why it matters. Do not invent missing information.
        """;

    private readonly IChatClient _summaryClient;
    private readonly IChatTokenEstimator _tokenEstimator;
    private readonly CompactionOptions _options;
    private readonly TimeProvider _timeProvider;

    public ContextCompactor(
        IChatClient summaryClient,
        IChatTokenEstimator tokenEstimator,
        IOptions<CompactionOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(summaryClient);
        ArgumentNullException.ThrowIfNull(tokenEstimator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var configuredOptions = options.Value;
        configuredOptions.Validate();

        _summaryClient = summaryClient;
        _tokenEstimator = tokenEstimator;
        _options = configuredOptions;
        _timeProvider = timeProvider;
    }

    public async ValueTask<CompactionResult> CompactIfNeededAsync(
        IReadOnlyList<ChatMessage> messages,
        CompactionTrigger trigger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ct.ThrowIfCancellationRequested();

        if (!_options.Enabled)
        {
            return new CompactionResult.NotNeeded(
                InputTokens: 0,
                ThresholdTokens: _options.AutoCompactThresholdTokens);
        }

        var original = messages.ToImmutableArray();
        var tokensBefore = CountTokens(original);
        var threshold = _options.AutoCompactThresholdTokens;
        var candidate = original;
        var steps = ImmutableArray.CreateBuilder<CompactionStep>();

        var microcompact = TryMicrocompact(candidate, tokensBefore);
        if (microcompact is MicrocompactAttempt.Applied appliedMicrocompact)
        {
            candidate = appliedMicrocompact.Messages;
            steps.Add(appliedMicrocompact.Step);
        }

        var tokensAfterMicrocompact = CountTokens(candidate);
        if (tokensAfterMicrocompact < threshold)
        {
            return steps.Count == 0
                ? new CompactionResult.NotNeeded(tokensBefore, threshold)
                : new CompactionResult.Applied(
                    candidate,
                    new CompactionReport(
                        trigger,
                        tokensBefore,
                        tokensAfterMicrocompact,
                        steps.ToImmutable()));
        }

        var fullCompact = await TryFullCompactAsync(candidate, tokensAfterMicrocompact, ct);
        if (fullCompact is FullCompactAttempt.Failure failed)
            return new CompactionResult.Failed(failed.Error, tokensBefore, threshold);

        var succeeded = (FullCompactAttempt.Success)fullCompact;
        if (succeeded.Step.TokensAfter >= threshold)
        {
            return new CompactionResult.Failed(
                new CompactionFailure(
                    CompactionFailureKind.StillOverLimit,
                    $"Compacted context is still {succeeded.Step.TokensAfter} tokens; threshold is {threshold}."),
                tokensBefore,
                threshold);
        }

        steps.Add(succeeded.Step);
        return new CompactionResult.Applied(
            succeeded.Messages,
            new CompactionReport(
                trigger,
                tokensBefore,
                succeeded.Step.TokensAfter,
                steps.ToImmutable()));
    }

    private int CountTokens(IReadOnlyList<ChatMessage> messages) =>
        (int)Math.Min(
            (long)_tokenEstimator.EstimateTokens(messages) + _options.FixedInputTokens,
            int.MaxValue);

    private MicrocompactAttempt TryMicrocompact(
        ImmutableArray<ChatMessage> messages,
        int tokensBefore)
    {
        if (tokensBefore < _options.AutoCompactThresholdTokens && !IsCacheCold(messages))
            return MicrocompactAttempt.NotApplied.Instance;

        var compactableCallIds = messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Where(call => _options.CompactableToolNames.Contains(call.Name))
            .Select(call => call.CallId)
            .ToHashSet(StringComparer.Ordinal);

        var resultLocations = new List<(int MessageIndex, int ContentIndex, FunctionResultContent Result)>();

        for (var messageIndex = 0; messageIndex < messages.Length; messageIndex++)
        {
            var contents = messages[messageIndex].Contents;
            for (var contentIndex = 0; contentIndex < contents.Count; contentIndex++)
            {
                if (contents[contentIndex] is FunctionResultContent result &&
                    compactableCallIds.Contains(result.CallId) &&
                    !string.Equals(result.Result as string, ClearedToolResultText, StringComparison.Ordinal))
                {
                    resultLocations.Add((messageIndex, contentIndex, result));
                }
            }
        }

        var clearCount = resultLocations.Count - _options.KeepRecentToolResults;
        if (clearCount <= 0)
            return MicrocompactAttempt.NotApplied.Instance;

        var array = messages.ToArray();
        var clearedIds = ImmutableArray.CreateBuilder<string>(clearCount);

        foreach (var messageGroup in resultLocations.Take(clearCount).GroupBy(location => location.MessageIndex))
        {
            var clone = array[messageGroup.Key].Clone();
            var contents = clone.Contents.ToList();

            foreach (var location in messageGroup)
            {
                contents[location.ContentIndex] = new FunctionResultContent(
                    location.Result.CallId,
                    ClearedToolResultText);
                clearedIds.Add(location.Result.CallId);
            }

            clone.Contents = contents;
            array[messageGroup.Key] = clone;
        }

        var compacted = array.ToImmutableArray();
        var tokensAfter = CountTokens(compacted);
        if (tokensBefore - tokensAfter < _options.MinimumMicrocompactSavingsTokens)
            return MicrocompactAttempt.NotApplied.Instance;

        return new MicrocompactAttempt.Applied(
            compacted,
            new CompactionStep.Microcompact(tokensBefore, tokensAfter, clearedIds.ToImmutable()));
    }

    private bool IsCacheCold(ImmutableArray<ChatMessage> messages)
    {
        var lastAssistant = messages.LastOrDefault(message => message.Role == ChatRole.Assistant);
        return lastAssistant?.CreatedAt is { } timestamp &&
            _timeProvider.GetUtcNow() - timestamp >= _options.MicrocompactColdAfter;
    }

    private async ValueTask<FullCompactAttempt> TryFullCompactAsync(
        ImmutableArray<ChatMessage> messages,
        int tokensBefore,
        CancellationToken ct)
    {
        var systemCount = 0;
        while (systemCount < messages.Length && messages[systemCount].Role == ChatRole.System)
            systemCount++;

        var userIndexes = new List<int>();
        for (var i = systemCount; i < messages.Length; i++)
            if (messages[i].Role == ChatRole.User)
                userIndexes.Add(i);

        int tailStart;
        if (_options.PreserveRecentUserTurns == 0)
        {
            tailStart = messages.Length;
        }
        else
        {
            if (userIndexes.Count <= _options.PreserveRecentUserTurns)
            {
                return new FullCompactAttempt.Failure(
                    new CompactionFailure(
                        CompactionFailureKind.NoCompactableHistory,
                        "No completed older user turn is available to summarize."));
            }

            tailStart = userIndexes[^_options.PreserveRecentUserTurns];
        }

        if (tailStart <= systemCount)
        {
            return new FullCompactAttempt.Failure(
                new CompactionFailure(
                    CompactionFailureKind.NoCompactableHistory,
                    "No conversation prefix is available to summarize."));
        }

        var summaryInput = new List<ChatMessage>(tailStart + 1);
        summaryInput.AddRange(messages.Take(tailStart).Select(message => message.Clone()));
        summaryInput.Add(new ChatMessage(ChatRole.User, SummaryInstruction));

        ChatResponse response;
        try
        {
            response = await _summaryClient.GetResponseAsync(
                summaryInput,
                new ChatOptions { MaxOutputTokens = _options.SummaryMaxOutputTokens },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FullCompactAttempt.Failure(
                new CompactionFailure(CompactionFailureKind.ProviderError, ex.Message));
        }

        var summary = ExtractSummary(response.Text);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return new FullCompactAttempt.Failure(
                new CompactionFailure(
                    CompactionFailureKind.InvalidSummary,
                    "The compaction model returned no text summary."));
        }

        var summaryMessage = new ChatMessage(
            ChatRole.User,
            $"This session continues from compacted history.\n\n<summary>\n{summary}\n</summary>");

        var candidate = ImmutableArray.CreateBuilder<ChatMessage>(
            systemCount + 1 + messages.Length - tailStart);
        candidate.AddRange(messages.Take(systemCount));
        candidate.Add(summaryMessage);
        candidate.AddRange(messages.Skip(tailStart));

        var compacted = candidate.ToImmutable();
        var tokensAfter = CountTokens(compacted);
        var summaryTokens = _tokenEstimator.EstimateTokens([summaryMessage]);

        return new FullCompactAttempt.Success(
            compacted,
            new CompactionStep.FullCompact(
                tokensBefore,
                tokensAfter,
                summaryTokens,
                messages.Length - tailStart));
    }

    private static string ExtractSummary(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return string.Empty;

        const string open = "<summary>";
        const string close = "</summary>";
        var start = responseText.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var end = responseText.LastIndexOf(close, StringComparison.OrdinalIgnoreCase);

        if (start < 0 || end <= start)
            return responseText.Trim();

        start += open.Length;
        return responseText[start..end].Trim();
    }

    private abstract record MicrocompactAttempt
    {
        private MicrocompactAttempt() { }

        public sealed record NotApplied : MicrocompactAttempt
        {
            public static readonly NotApplied Instance = new();
        }

        public sealed record Applied(
            ImmutableArray<ChatMessage> Messages,
            CompactionStep.Microcompact Step) : MicrocompactAttempt;
    }

    private abstract record FullCompactAttempt
    {
        private FullCompactAttempt() { }

        public sealed record Success(
            ImmutableArray<ChatMessage> Messages,
            CompactionStep.FullCompact Step) : FullCompactAttempt;

        public sealed record Failure(CompactionFailure Error) : FullCompactAttempt;
    }
}
