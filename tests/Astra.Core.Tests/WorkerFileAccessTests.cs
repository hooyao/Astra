using System.Runtime.CompilerServices;
using Astra.Core.Compaction;
using Astra.Core.Coordination;
using Astra.Core.Files;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Astra.Core.Tests;

public sealed class WorkerFileAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AstraWorkerFileAccessTests-{Guid.NewGuid():N}");

    public WorkerFileAccessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task WriteWorker_ReadsEditsAndReportsTrustedChangedPath()
    {
        var path = Path.Combine(_root, "worker.txt");
        await File.WriteAllTextAsync(path, "hello world");
        await using var provider = CreateProvider();
        await using var session = await provider
            .GetRequiredService<IWorkerSessionFactory>()
            .CreateAsync(
                new WorkerTaskId("task-write"),
                new WorkerId("worker-write"),
                new WorkerRequest("write", "update worker.txt", WorkerAccessMode.Write));

        var completion = await session.RunAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Completed, completion.Status);
        Assert.Equal("hello astra", await File.ReadAllTextAsync(path));
        Assert.Equal([path], completion.ChangedPaths);
    }

    [Fact]
    public async Task ReadOnlyWorker_CannotActivateEditExecutor()
    {
        var path = Path.Combine(_root, "worker.txt");
        await File.WriteAllTextAsync(path, "hello world");
        await using var provider = CreateProvider();
        await using var session = await provider
            .GetRequiredService<IWorkerSessionFactory>()
            .CreateAsync(
                new WorkerTaskId("task-read"),
                new WorkerId("worker-read"),
                new WorkerRequest("read", "inspect worker.txt", WorkerAccessMode.ReadOnly));

        var completion = await session.RunAsync(CancellationToken.None);

        Assert.Equal(WorkerStatus.Completed, completion.Status);
        Assert.Equal("hello world", await File.ReadAllTextAsync(path));
        Assert.Empty(completion.ChangedPaths);
    }

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new WorkspaceFileSystem(_root, [_root]));
        services.AddSingleton<FileWriteCoordinator>();
        services.AddScoped<FileObservationStore>();
        services.AddScoped<IChatClient, FileEditingWorkerClient>();
        services.AddScoped<UsageTrackingChatClient>();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IChatTokenEstimator, RoughChatTokenEstimator>();
        services.AddKeyedTransient<IToolExecutor, ReadFileTool>(ReadFileTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, EditFileTool>(EditFileTool.ToolName);
        services.AddScoped<IToolExecutorFactory, DependencyInjectionToolExecutorFactory>();
        services.AddKeyedScoped<AgentLoop>(AgentServiceKeys.WorkerLoop, (provider, _) =>
        {
            var files = provider.GetRequiredService<WorkspaceFileSystem>();
            return new AgentLoop(
                provider.GetRequiredService<UsageTrackingChatClient>(),
                [
                    ReadFileTool.CreateDefinition(files),
                    EditFileTool.CreateDefinition(files),
                ],
                systemPrompt: "Test worker",
                toolExecutorFactory: provider.GetRequiredService<IToolExecutorFactory>());
        });
        services.AddScoped<IWorker, AgentLoopWorker>();
        services.AddSingleton<IWorkerSessionFactory, DependencyInjectionWorkerSessionFactory>();
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private sealed class FileEditingWorkerClient : IChatClient
    {
        private int _callCount;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            switch (Interlocked.Increment(ref _callCount))
            {
                case 1:
                    yield return ToolCall(
                        "read-call",
                        ReadFileTool.ToolName,
                        new Dictionary<string, object?> { ["file_path"] = "worker.txt" });
                    break;
                case 2:
                    yield return ToolCall(
                        "edit-call",
                        EditFileTool.ToolName,
                        new Dictionary<string, object?>
                        {
                            ["file_path"] = "worker.txt",
                            ["old_string"] = "hello world",
                            ["new_string"] = "hello astra",
                        });
                    break;
                default:
                    yield return new ChatResponseUpdate(ChatRole.Assistant, ValidReportJson);
                    break;
            }
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Streaming only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static ChatResponseUpdate ToolCall(
            string callId,
            string name,
            IDictionary<string, object?> arguments) =>
            new(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, name, arguments)]);
    }

    private const string ValidReportJson = """
        {
          "summary": "Worker finished.",
          "findings": [],
          "changes": [{ "path": "worker.txt", "description": "Updated greeting." }],
          "verification": [],
          "risks": [],
          "open_questions": []
        }
        """;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
