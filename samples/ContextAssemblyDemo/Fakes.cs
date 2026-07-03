using System.Runtime.CompilerServices;
using Astra.Core.Context;
using Microsoft.Extensions.AI;

namespace ContextAssemblyDemo;

/// <summary>A fake model that snapshots the messages it receives, then ends the turn.</summary>
internal sealed class CapturingClient : IChatClient
{
    public List<List<ChatMessage>> Calls { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Calls.Add(messages.ToList());
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> m, ChatOptions? o = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public object? GetService(Type t, object? k = null) => null;
    public void Dispose() { }
}

/// <summary>A layer-c provider that hangs past the deadline (respects cancellation).</summary>
internal sealed class HangingProvider : IAttachmentProvider
{
    public string Name => "slow-mcp";
    public async ValueTask<string?> GetAsync(CancellationToken ct = default)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        return "SHOULD-NEVER-APPEAR";
    }
}
