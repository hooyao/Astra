using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Astra.Core.Files;

/// <summary>Create or overwrite a complete UTF-8 text file.</summary>
public sealed class WriteFileTool(
    WorkspaceFileSystem fileSystem,
    FileObservationStore observations,
    FileWriteCoordinator writes) : IToolExecutor
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
            "Creates missing parent directories. Read an existing file first; Astra rejects a complete replacement " +
            "if its content changed since that read. Prefer Edit for targeted changes.",
            Schema,
            static _ => ToolAction.Write);
    }

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ToolOutput.Result(await WriteAsync(arguments, ct));
    }

    private async Task<string> WriteAsync(
        IDictionary<string, object?>? arguments,
        CancellationToken ct)
    {
        var requestedPath = FileToolArguments.RequireString(arguments, "file_path");
        var content = FileToolArguments.RequirePresentString(arguments, "content");
        var path = fileSystem.ResolvePath(requestedPath);

        using var writeLease = await writes.AcquireAsync(path, ct);
        var overwrite = File.Exists(path);
        if (overwrite)
        {
            if (!observations.TryGet(path, out var observedVersion))
            {
                return "Error: File already exists and has not been read by this agent. Read it before replacing it.";
            }

            var snapshot = await Utf8TextFile.ReadAllAsync(path, ct);
            if (snapshot.Version != observedVersion)
            {
                observations.Invalidate(path);
                return "Error: File changed since this agent last read it. Read it again before replacing it.";
            }
        }
        else if (observations.TryGet(path, out _))
        {
            observations.Invalidate(path);
            return "Error: File was removed since this agent last observed it. Read the path again before writing it.";
        }

        var newVersion = Utf8TextFile.ComputeVersion(content, emitUtf8Bom: false);
        try
        {
            await fileSystem.WriteTextAtomicallyAsync(
                path,
                content,
                overwrite,
                ct,
                createParentDirectories: true);
        }
        catch (IOException) when (!overwrite && File.Exists(path))
        {
            return "Error: File was created by another writer. Read it before replacing it.";
        }

        observations.RecordWrite(path, newVersion);
        return $"Wrote {Encoding.UTF8.GetByteCount(content):N0} UTF-8 bytes to {fileSystem.DisplayPath(path)}.";
    }
}
