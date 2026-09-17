using Astra.Core;
using Astra.Core.Files;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== D9 File Freshness Demo ===");

var root = Path.Combine(Path.GetTempPath(), $"AstraFileFreshnessDemo-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    var fileSystem = new WorkspaceFileSystem(root, [root]);
    var writes = new FileWriteCoordinator();
    var agentA = new FileObservationStore();
    var agentB = new FileObservationStore();
    var sharedPath = Path.Combine(root, "shared.txt");
    await File.WriteAllTextAsync(sharedPath, "hello world");

    Console.WriteLine("\n1. Two agents observe the same H0");
    await ReadAsync(fileSystem, agentA, "shared.txt");
    await ReadAsync(fileSystem, agentB, "shared.txt");
    Console.WriteLine("   A read H0; B read H0");

    Console.WriteLine("\n2. A writes H1; B's H0 becomes stale");
    Console.WriteLine($"   A: {await EditAsync(fileSystem, writes, agentA, "shared.txt", "hello world", "hello astra")}");
    await RequireContentAsync(sharedPath, "hello astra");
    Console.WriteLine($"   disk: {await File.ReadAllTextAsync(sharedPath)}");
    var staleResult = await EditAsync(fileSystem, writes, agentB, "shared.txt", "hello world", "hello beta");
    Console.WriteLine($"   B: {staleResult}");
    if (!staleResult.Contains("changed since this agent last read it", StringComparison.Ordinal))
        throw new InvalidOperationException("The stale writer did not receive a freshness failure.");
    await RequireContentAsync(sharedPath, "hello astra");
    Console.WriteLine($"   disk after rejected B edit: {await File.ReadAllTextAsync(sharedPath)}");

    Console.WriteLine("\n3. B reads once and retries from H1");
    await ReadAsync(fileSystem, agentB, "shared.txt");
    Console.WriteLine($"   B: {await EditAsync(fileSystem, writes, agentB, "shared.txt", "hello astra", "hello beta")}");
    await RequireContentAsync(sharedPath, "hello beta");
    Console.WriteLine($"   disk: {await File.ReadAllTextAsync(sharedPath)}");

    var sequentialPath = Path.Combine(root, "sequential.txt");
    await File.WriteAllTextAsync(sequentialPath, "hello world");
    var sequentialAgent = new FileObservationStore();
    await ReadAsync(fileSystem, sequentialAgent, "sequential.txt");

    Console.WriteLine("\n4. One agent performs ordered edits without a redundant Read");
    Console.WriteLine($"   edit 1: {await EditAsync(fileSystem, writes, sequentialAgent, "sequential.txt", "hello world", "hello astra")}");
    await RequireContentAsync(sequentialPath, "hello astra");
    Console.WriteLine($"   edit 2: {await EditAsync(fileSystem, writes, sequentialAgent, "sequential.txt", "hello astra", "hello beta")}");
    await RequireContentAsync(sequentialPath, "hello beta");
    Console.WriteLine($"   disk: {await File.ReadAllTextAsync(sequentialPath)}");

    Console.WriteLine("\nPASS: changed files require one fresh Read; same-agent writes advance their own snapshot.");
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
}

static Task<string> ReadAsync(
    WorkspaceFileSystem fileSystem,
    FileObservationStore observations,
    string path) =>
    ExecuteAsync(
        new ReadFileTool(fileSystem, observations),
        new Dictionary<string, object?> { ["file_path"] = path });

static Task<string> EditAsync(
    WorkspaceFileSystem fileSystem,
    FileWriteCoordinator writes,
    FileObservationStore observations,
    string path,
    string oldText,
    string newText) =>
    ExecuteAsync(
        new EditFileTool(fileSystem, observations, writes),
        new Dictionary<string, object?>
        {
            ["file_path"] = path,
            ["old_string"] = oldText,
            ["new_string"] = newText,
        });

static async Task<string> ExecuteAsync(
    IToolExecutor tool,
    IDictionary<string, object?> arguments)
{
    await foreach (var output in tool.ExecuteAsync(arguments, CancellationToken.None))
    {
        if (output is ToolOutput.Result(var text))
            return text;
    }

    throw new InvalidOperationException("The file tool returned no result.");
}

static async Task RequireContentAsync(string path, string expected)
{
    if (await File.ReadAllTextAsync(path) != expected)
        throw new InvalidOperationException($"Unexpected content in {Path.GetFileName(path)}.");
}
