using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Astra.Core.Files;

/// <summary>Replace exact text in an existing UTF-8 file inside the workspace.</summary>
public sealed class EditFileTool(WorkspaceFileSystem fileSystem) : ITool
{
    public string Name => "Edit";

    public string Description =>
        $"Edit an existing UTF-8 text file ({fileSystem.AccessDescription}) " +
        "by exact ordinal text replacement. By default old_string must occur exactly once.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "Relative or absolute path to the file to modify." },
            "old_string": { "type": "string", "minLength": 1, "description": "Exact text to replace." },
            "new_string": { "type": "string", "description": "Replacement text; may be empty to delete old_string." },
            "replace_all": { "type": "boolean", "default": false, "description": "Replace every occurrence of old_string instead of requiring exactly one." }
          },
          "required": ["file_path", "old_string", "new_string"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Write;

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var requestedPath = FileToolArguments.RequireString(arguments, "file_path");
        var oldText = FileToolArguments.RequireNonEmptyString(arguments, "old_string");
        var newText = FileToolArguments.RequirePresentString(arguments, "new_string");
        var replaceAll = FileToolArguments.OptionalBool(arguments, "replace_all", fallback: false);
        var path = fileSystem.ResolvePath(requestedPath);

        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", fileSystem.DisplayPath(path));

        var snapshot = await Utf8TextFile.ReadAllAsync(path, ct);
        var occurrences = CountOccurrences(snapshot.Content, oldText);
        if (occurrences == 0)
            throw new InvalidOperationException("old_string was not found; the file was not changed.");
        if (!replaceAll && occurrences != 1)
        {
            throw new InvalidOperationException(
                $"old_string occurs {occurrences} times; provide a unique block or set replace_all=true.");
        }

        var updated = replaceAll
            ? snapshot.Content.Replace(oldText, newText, StringComparison.Ordinal)
            : ReplaceOnce(snapshot.Content, oldText, newText);

        await fileSystem.WriteTextAtomicallyAsync(
            path,
            updated,
            overwrite: true,
            ct,
            emitUtf8Bom: snapshot.HasByteOrderMark);
        yield return new ToolOutput.Result(
            $"Edited {fileSystem.DisplayPath(path)}: replaced {(replaceAll ? occurrences : 1)} occurrence(s); " +
            $"file is now {Encoding.UTF8.GetByteCount(updated):N0} UTF-8 bytes.");
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = content.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private static string ReplaceOnce(string content, string oldText, string newText)
    {
        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        return string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
    }
}
