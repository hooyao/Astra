using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Astra.Core.Coordination;

/// <summary>
/// Per-worker telemetry wrapper. The worker dependency-injection scope owns the
/// underlying client and disposes both registrations at the end of execution.
/// </summary>
public sealed class UsageTrackingChatClient(IChatClient inner) : IChatClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _additionalCounts = new(StringComparer.Ordinal);
    private long _inputTokens;
    private long _outputTokens;
    private long _cachedInputTokens;
    private long _reasoningTokens;
    private long _totalTokens;
    private int _modelCalls;
    private string _lastResponseText = string.Empty;

    public string LastResponseText
    {
        get
        {
            lock (_gate)
                return _lastResponseText;
        }
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _modelCalls);
        var response = await inner.GetResponseAsync(messages, options, cancellationToken);
        Record(response);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _modelCalls);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in inner.GetStreamingResponseAsync(
                           messages,
                           options,
                           cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        if (updates.Count > 0)
            Record(updates.ToChatResponse());
    }

    public WorkerUsage Snapshot(int toolCalls, long durationMilliseconds)
    {
        lock (_gate)
        {
            return new WorkerUsage(
                _inputTokens,
                _outputTokens,
                _cachedInputTokens,
                _reasoningTokens,
                _totalTokens,
                Volatile.Read(ref _modelCalls),
                toolCalls,
                durationMilliseconds,
                new Dictionary<string, long>(_additionalCounts, StringComparer.Ordinal));
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
        // The dependency-injection scope owns the underlying provider client.
    }

    private void Record(ChatResponse response)
    {
        lock (_gate)
        {
            _lastResponseText = response.Text ?? string.Empty;
            var usage = response.Usage;
            if (usage is null)
                return;

            var input = usage.InputTokenCount ?? 0;
            var output = usage.OutputTokenCount ?? 0;
            _inputTokens += input;
            _outputTokens += output;
            _cachedInputTokens += usage.CachedInputTokenCount ?? 0;
            _reasoningTokens += usage.ReasoningTokenCount ?? 0;
            _totalTokens += usage.TotalTokenCount ?? input + output;

            if (usage.AdditionalCounts is null)
                return;
            foreach (var (name, count) in usage.AdditionalCounts)
                _additionalCounts[name] = _additionalCounts.GetValueOrDefault(name) + count;
        }
    }
}
