using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Astra.Core.Tests;

public sealed class ToolActivationTests
{
    private const string ToolName = "probe";

    [Fact]
    public async Task KeyedTransientExecutor_IsCreatedOnlyForEachAdmittedInvocation()
    {
        var activations = new ActivationLog();
        var services = new ServiceCollection();
        services.AddSingleton(activations);
        services.AddKeyedTransient<IToolExecutor, ProbeExecutor>(ToolName);
        services.AddScoped<IToolExecutorFactory, DependencyInjectionToolExecutorFactory>();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var executorFactory = scope.ServiceProvider.GetRequiredService<IToolExecutorFactory>();
        var definition = new ToolDefinition(
            ToolName,
            "Records invocation-time activation.",
            ToolSchema.Parse("{\"type\":\"object\"}"),
            static _ => ToolAction.Read);
        var textOnlyClient = new TextOnlyChatClient();
        var unusedLoop = new AgentLoop(
            textOnlyClient,
            [definition],
            toolExecutorFactory: executorFactory);

        await foreach (var _ in unusedLoop.SubmitAsync("do not use a tool")) { }
        Assert.Empty(activations.InstanceIds);
        var advertised = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(textOnlyClient.AdvertisedTools!));
        Assert.Equal(ToolName, advertised.Name);
        Assert.Equal(JsonValueKind.Object, advertised.JsonSchema.ValueKind);

        var loop = new AgentLoop(
            new ProbeChatClient(),
            [definition],
            toolExecutorFactory: executorFactory);

        Assert.Empty(activations.InstanceIds);

        await foreach (var _ in loop.SubmitAsync("first")) { }
        await foreach (var _ in loop.SubmitAsync("second")) { }

        Assert.Equal(2, activations.InstanceIds.Count);
        Assert.Equal(2, activations.InstanceIds.Distinct().Count());
    }

    private sealed class ActivationLog
    {
        public ConcurrentBag<Guid> InstanceIds { get; } = [];
    }

    private sealed class ProbeExecutor(ActivationLog activations) : IToolExecutor
    {
        private readonly Guid _instanceId = RecordActivation(activations);

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return new ToolOutput.Result(_instanceId.ToString("N"));
        }

        private static Guid RecordActivation(ActivationLog activations)
        {
            var id = Guid.NewGuid();
            activations.InstanceIds.Add(id);
            return id;
        }
    }

    private sealed class ProbeChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (messages.Last().Role == ChatRole.Tool)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent($"call-{Guid.NewGuid():N}", ToolName, null)]);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class TextOnlyChatClient : IChatClient
    {
        public IReadOnlyList<AITool>? AdvertisedTools { get; private set; }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            AdvertisedTools = options?.Tools?.ToArray();
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
