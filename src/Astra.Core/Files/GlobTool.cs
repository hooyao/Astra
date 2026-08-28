using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Astra.Core.Files;

/// <summary>Find files by a familiar coding-agent glob contract.</summary>
public sealed class GlobTool(WorkspaceFileSystem fileSystem) : ITool
{
    private const int MaximumResults = 100;

    public string Name => "Glob";

    public string Description =>
        $"Find files by glob pattern ({fileSystem.AccessDescription}). " +
        "Supports *, **, ?, and brace alternatives such as *.{{cs,csproj}}. " +
        $"Returns at most {MaximumResults} files and skips version-control metadata and symbolic links.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "minLength": 1, "description": "Glob pattern to match, for example **/*.cs or *.{ts,tsx}." },
            "path": { "type": "string", "description": "Directory to search. Omit to use the current working directory." }
          },
          "required": ["pattern"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Read;

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var pattern = FileToolArguments.RequireNonEmptyString(arguments, "pattern");
        var requestedPath = FileToolArguments.OptionalString(
            arguments,
            "path",
            fileSystem.BaseDirectory);
        var root = fileSystem.ResolvePath(requestedPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Directory not found: {fileSystem.DisplayPath(root)}");

        var matcher = FileSearch.CreateMatcher(pattern, matchFilenameAtAnyDepth: false);
        var matches = new List<(string Path, DateTime LastWriteTimeUtc)>();
        foreach (var file in FileSearch.EnumerateFiles(root, ct))
        {
            if (FileSearch.IsMatch(matcher, root, file))
                matches.Add((file, File.GetLastWriteTimeUtc(file)));
        }

        var ordered = matches
            .OrderByDescending(match => match.LastWriteTimeUtc)
            .ThenBy(match => match.Path, StringComparerFromPlatform())
            .Take(MaximumResults)
            .Select(match => fileSystem.DisplayPath(match.Path))
            .ToArray();
        var truncated = matches.Count > MaximumResults;

        var result = ordered.Length == 0
            ? "No files found."
            : $"Found {matches.Count:N0} file(s){(truncated ? $"; showing the first {MaximumResults}." : ".")}\n" +
              string.Join('\n', ordered);
        yield return new ToolOutput.Result(result);
        await Task.CompletedTask;
    }

    private static StringComparer StringComparerFromPlatform() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
