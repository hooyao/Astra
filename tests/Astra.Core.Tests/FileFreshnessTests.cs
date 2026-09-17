using Astra.Core.Files;
using Xunit;

namespace Astra.Core.Tests;

public sealed class FileFreshnessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AstraFileFreshnessTests-{Guid.NewGuid():N}");
    private readonly WorkspaceFileSystem _fileSystem;
    private readonly FileWriteCoordinator _writes = new();

    public FileFreshnessTests()
    {
        Directory.CreateDirectory(_root);
        _fileSystem = new WorkspaceFileSystem(_root, [_root]);
    }

    [Fact]
    public async Task Edit_RequiresThisAgentToReadExistingFile()
    {
        var path = Path.Combine(_root, "unread.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var observations = new FileObservationStore();

        var result = await EditAsync(observations, "unread.txt", "hello world", "hello astra");

        Assert.Contains("has not been read by this agent", result);
        Assert.Equal("hello world", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Edit_RejectsUnrelatedChangeEvenWhenOldStringRemainsUnique()
    {
        var path = Path.Combine(_root, "changed.txt");
        await File.WriteAllTextAsync(path, "header-v0\nhello world");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "changed.txt");

        await File.WriteAllTextAsync(path, "header-v1\nhello world");
        var result = await EditAsync(observations, "changed.txt", "hello world", "hello astra");

        Assert.Contains("changed since this agent last read it", result);
        Assert.Equal("header-v1\nhello world", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task TwoAgents_StaleWriterReadsAgainAndRecovers()
    {
        var path = Path.Combine(_root, "workers.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var agentA = new FileObservationStore();
        var agentB = new FileObservationStore();
        await ReadAsync(agentA, "workers.txt");
        await ReadAsync(agentB, "workers.txt");

        var first = await EditAsync(agentA, "workers.txt", "hello world", "hello astra");
        var stale = await EditAsync(agentB, "workers.txt", "hello world", "hello beta");

        Assert.Contains("Edited workers.txt", first);
        Assert.Contains("changed since this agent last read it", stale);
        Assert.Equal("hello astra", await File.ReadAllTextAsync(path));

        await ReadAsync(agentB, "workers.txt");
        var recovered = await EditAsync(agentB, "workers.txt", "hello astra", "hello beta");

        Assert.Contains("Edited workers.txt", recovered);
        Assert.Equal("hello beta", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SameAgent_SequentialEditsAdvanceObservationWithoutAnotherRead()
    {
        var path = Path.Combine(_root, "sequential.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "sequential.txt");

        var first = await EditAsync(observations, "sequential.txt", "hello world", "hello astra");
        var second = await EditAsync(observations, "sequential.txt", "hello astra", "hello beta");

        Assert.Contains("Edited sequential.txt", first);
        Assert.Contains("Edited sequential.txt", second);
        Assert.Equal("hello beta", await File.ReadAllTextAsync(path));
        Assert.Equal([path], observations.SnapshotChangedPaths());
    }

    [Fact]
    public async Task BoundedRead_HashesCompleteFileNotOnlyReturnedRange()
    {
        var path = Path.Combine(_root, "bounded.txt");
        await File.WriteAllTextAsync(path, "outside-v0\ntarget\ntail");
        var observations = new FileObservationStore();
        var read = new ReadFileTool(_fileSystem, observations);
        await ExecuteAsync(read, new Dictionary<string, object?>
        {
            ["file_path"] = "bounded.txt",
            ["offset"] = 2,
            ["limit"] = 1,
        });

        await File.WriteAllTextAsync(path, "outside-v1\ntarget\ntail");
        var result = await EditAsync(observations, "bounded.txt", "target", "updated");

        Assert.Contains("changed since this agent last read it", result);
        Assert.Equal("outside-v1\ntarget\ntail", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MetadataOnlyChange_DoesNotInvalidateContentObservation()
    {
        var path = Path.Combine(_root, "metadata.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "metadata.txt");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var result = await EditAsync(observations, "metadata.txt", "hello world", "hello astra");

        Assert.Contains("Edited metadata.txt", result);
        Assert.Equal("hello astra", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CompleteWrite_RejectsChangedExistingFile()
    {
        var path = Path.Combine(_root, "replace.txt");
        await File.WriteAllTextAsync(path, "version zero");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "replace.txt");
        await File.WriteAllTextAsync(path, "external version");

        var write = new WriteFileTool(_fileSystem, observations, _writes);
        var result = await ExecuteAsync(write, new Dictionary<string, object?>
        {
            ["file_path"] = "replace.txt",
            ["content"] = "agent version",
        });

        Assert.Contains("changed since this agent last read it", result);
        Assert.Equal("external version", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CompleteWrite_RequiresReadForAnExistingFile()
    {
        var path = Path.Combine(_root, "unread-replace.txt");
        await File.WriteAllTextAsync(path, "original");
        var observations = new FileObservationStore();
        var write = new WriteFileTool(_fileSystem, observations, _writes);

        var result = await ExecuteAsync(write, new Dictionary<string, object?>
        {
            ["file_path"] = "unread-replace.txt",
            ["content"] = "replacement",
        });

        Assert.Contains("has not been read by this agent", result);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DeletedObservedFile_IsReportedAsChangedBeforeRecreation()
    {
        var path = Path.Combine(_root, "deleted.txt");
        await File.WriteAllTextAsync(path, "original");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "deleted.txt");
        File.Delete(path);
        var write = new WriteFileTool(_fileSystem, observations, _writes);

        var result = await ExecuteAsync(write, new Dictionary<string, object?>
        {
            ["file_path"] = "deleted.txt",
            ["content"] = "replacement",
        });

        Assert.Contains("removed since this agent last observed it", result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CancelledEdit_DoesNotChangeFile()
    {
        var path = Path.Combine(_root, "cancelled-edit.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "cancelled-edit.txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EditAsync(
                observations,
                "cancelled-edit.txt",
                "hello world",
                "hello astra",
                cancellation.Token));
        Assert.Equal("hello world", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Coordinator_SerializesSamePathButNotDifferentPaths()
    {
        var pathA = Path.Combine(_root, "a.txt");
        var pathB = Path.Combine(_root, "b.txt");
        using var first = await _writes.AcquireAsync(pathA, CancellationToken.None);

        var samePath = _writes.AcquireAsync(pathA, CancellationToken.None).AsTask();
        var differentPath = _writes.AcquireAsync(pathB, CancellationToken.None).AsTask();

        Assert.False(samePath.IsCompleted);
        Assert.True(differentPath.IsCompletedSuccessfully);
        using var differentLease = await differentPath;

        first.Dispose();
        using var sameLease = await samePath;
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    public async Task WriteResult_ReleasesGateBeforeConsumerContinues(string toolName)
    {
        var path = Path.Combine(_root, "result-lifetime.txt");
        await File.WriteAllTextAsync(path, "hello world");
        var observations = new FileObservationStore();
        await ReadAsync(observations, "result-lifetime.txt");

        IToolExecutor firstTool = toolName == "Edit"
            ? new EditFileTool(_fileSystem, observations, _writes)
            : new WriteFileTool(_fileSystem, observations, _writes);
        var arguments = new Dictionary<string, object?>
        {
            ["file_path"] = "result-lifetime.txt",
            ["old_string"] = "hello world",
            ["new_string"] = "hello astra",
            ["content"] = "hello astra",
        };
        await using var firstCall = firstTool.ExecuteAsync(arguments, CancellationToken.None)
            .GetAsyncEnumerator();
        Assert.True(await firstCall.MoveNextAsync());
        Assert.IsType<ToolOutput.Result>(firstCall.Current);

        // A consumer may keep the first iterator parked on its final result.
        // The next writer must not depend on that consumer resuming or disposing it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var second = await EditAsync(
            observations, "result-lifetime.txt", "hello astra", "hello beta", timeout.Token);

        Assert.Contains("Edited result-lifetime.txt", second);
        Assert.Equal("hello beta", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Coordinator_WaitHonorsCancellation()
    {
        var path = Path.Combine(_root, "cancel.txt");
        using var first = await _writes.AcquireAsync(path, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _writes.AcquireAsync(path, cancellation.Token).AsTask());
    }

    private Task<string> ReadAsync(FileObservationStore observations, string path) =>
        ExecuteAsync(
            new ReadFileTool(_fileSystem, observations),
            new Dictionary<string, object?> { ["file_path"] = path });

    private Task<string> EditAsync(
        FileObservationStore observations,
        string path,
        string oldText,
        string newText,
        CancellationToken ct = default) =>
        ExecuteAsync(
            new EditFileTool(_fileSystem, observations, _writes),
            new Dictionary<string, object?>
            {
                ["file_path"] = path,
                ["old_string"] = oldText,
                ["new_string"] = newText,
            },
            ct);

    private static async Task<string> ExecuteAsync(
        IToolExecutor tool,
        IDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var outputs = new List<ToolOutput>();
        await foreach (var output in tool.ExecuteAsync(arguments, ct))
            outputs.Add(output);

        return Assert.IsType<ToolOutput.Result>(Assert.Single(outputs)).Text;
    }

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
