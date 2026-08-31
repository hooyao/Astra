using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Astra.Providers;

/// <summary>Scoped provider client constructed from strongly typed LLM options.</summary>
public sealed class ConfiguredChatClient : IChatClient
{
    private readonly IChatClient _inner;

    public ConfiguredChatClient(IOptions<LlmConfig> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = ChatClientFactory.Create(options.Value);
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetResponseAsync(messages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
