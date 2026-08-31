using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Astra.Core.Files;

/// <summary>Create or overwrite a complete UTF-8 text file.</summary>
public sealed class WriteFileTool(WorkspaceFileSystem fileSystem) : IToolExecutor
{
    public const string ToolName = "Write";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Relative or absolute path to the file to write." },
            "content": { "type": "string", "description": "Complete content to write to the file." }
          },
          "required": ["file_path", "content"],
          "additionalProperties": false
        }
        """);

    public static ToolDefinition CreateDefinition(WorkspaceFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new ToolDefinition(
            ToolName,
            $"Write complete UTF-8 file content ({fileSystem.AccessDescription}). " +
            "Creates missing parent directories and overwrites an existing file. Prefer Edit for targeted changes.",
            Schema,
            static _ => ToolAction.Write);
    }

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var requestedPath = FileToolArguments.RequireString(arguments, "file_path");
        var content = FileToolArguments.RequirePresentString(arguments, "content");
        var path = fileSystem.ResolvePath(requestedPath);

        await fileSystem.WriteTextAtomicallyAsync(
            path,
            content,
            overwrite: true,
            ct,
            createParentDirectories: true);
        yield return new ToolOutput.Result(
            $"Wrote {Encoding.UTF8.GetByteCount(content):N0} UTF-8 bytes to {fileSystem.DisplayPath(path)}.");
    }
}
