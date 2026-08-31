using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Astra.Core.Files;

/// <summary>Read a bounded range of a UTF-8 text file inside the workspace.</summary>
public sealed class ReadFileTool(WorkspaceFileSystem fileSystem) : IToolExecutor
{
    public const string ToolName = "Read";

    private const int DefaultLineLimit = 2_000;
    private const int MaximumLineLimit = 10_000;
    private const int MaximumCharacters = 500_000;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Relative or absolute path to the file to read." },
            "offset": { "type": "integer", "minimum": 1, "description": "First 1-based line to return." },
            "limit": { "type": "integer", "minimum": 1, "maximum": 10000, "description": "Maximum lines to return." }
          },
          "required": ["file_path"],
          "additionalProperties": false
        }
        """);

    public static ToolDefinition CreateDefinition(WorkspaceFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new ToolDefinition(
            ToolName,
            $"Read a UTF-8 text file ({fileSystem.AccessDescription}). " +
            "Use offset and limit for large files. Returned content preserves the file's original line terminators.",
            Schema,
            static _ => ToolAction.Read);
    }

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var requestedPath = FileToolArguments.RequireString(arguments, "file_path");
        var offset = FileToolArguments.OptionalInt(arguments, "offset", 1, 1, int.MaxValue);
        var limit = FileToolArguments.OptionalInt(
            arguments, "limit", DefaultLineLimit, 1, MaximumLineLimit);
        var path = fileSystem.ResolvePath(requestedPath);

        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", fileSystem.DisplayPath(path));

        await using var reader = Utf8TextFile.OpenLineReader(path);

        for (var lineNumber = 1; lineNumber < offset; lineNumber++)
        {
            if (await reader.ReadLineAsync(ct) is null)
            {
                yield return new ToolOutput.Result(
                    $"File: {fileSystem.DisplayPath(path)}\nRequested offset {offset} is past end of file.");
                yield break;
            }
        }

        var content = new StringBuilder();
        var linesRead = 0;
        var truncatedByCharacters = false;
        while (linesRead < limit)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            var required = line.Value.Length;
            if (content.Length + required > MaximumCharacters)
            {
                truncatedByCharacters = true;
                break;
            }

            content.Append(line.Value.Content);
            content.Append(line.Value.Terminator);
            linesRead++;
        }

        var endLine = linesRead == 0 ? offset : offset + linesRead - 1;
        var hasMoreLines = !truncatedByCharacters &&
            linesRead == limit &&
            await reader.ReadLineAsync(ct) is not null;
        var suffix = truncatedByCharacters
            ? $"\n\n[Output truncated before line {offset + linesRead} at {MaximumCharacters:N0} characters. " +
              "Use a narrower range or Grep.]"
            : hasMoreLines
                ? $"\n\n[More content available. Continue with offset={endLine + 1}.]"
                : string.Empty;

        yield return new ToolOutput.Result(
            $"File: {fileSystem.DisplayPath(path)}\nLines: {offset}-{endLine}\n\n{content}{suffix}");
    }
}
