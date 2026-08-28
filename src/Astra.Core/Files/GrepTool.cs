using Microsoft.Extensions.FileSystemGlobbing;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Core.Files;

/// <summary>Search UTF-8 file contents using a Claude Code-compatible core schema.</summary>
public sealed class GrepTool(WorkspaceFileSystem fileSystem) : ITool
{
    private const int DefaultHeadLimit = 250;
    private const int MaximumHeadLimit = 10_000;
    private const int MaximumOutputCharacters = 20_000;
    private const int MaximumPreviewCharacters = 500;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public string Name => "Grep";

    public string Description =>
        $"Search UTF-8 file contents with a .NET regular expression ({fileSystem.AccessDescription}). " +
        "The path may be a file or directory. Use output_mode=content to get 1-based line and column " +
        "before a targeted Read or Edit. Results are bounded and version-control metadata and symbolic links are skipped.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "pattern": { "type": "string", "minLength": 1, "description": "The regular expression pattern to search for in file contents." },
            "path": { "type": "string", "description": "File or directory to search. Omit to use the current working directory." },
            "glob": { "type": "string", "description": "Glob pattern for files in a directory search, for example *.cs or *.{ts,tsx}." },
            "output_mode": { "type": "string", "enum": ["content", "files_with_matches", "count"], "default": "files_with_matches", "description": "content returns matching lines; files_with_matches returns paths; count returns occurrence counts per file." },
            "-i": { "type": "boolean", "default": false, "description": "Use case-insensitive matching." },
            "head_limit": { "type": "integer", "minimum": 0, "maximum": 10000, "default": 250, "description": "Maximum entries after offset. Use 0 for no entry limit; output remains character-bounded." },
            "offset": { "type": "integer", "minimum": 0, "default": 0, "description": "Skip this many matching entries before returning results." }
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
        if (pattern.Contains('\r') || pattern.Contains('\n') || pattern.Contains('\0'))
            throw new ArgumentException("Argument 'pattern' must be a single-line regular expression.");

        var requestedPath = FileToolArguments.OptionalString(
            arguments,
            "path",
            fileSystem.BaseDirectory);
        var glob = FileToolArguments.OptionalString(arguments, "glob", string.Empty);
        var outputMode = ParseOutputMode(
            FileToolArguments.OptionalString(arguments, "output_mode", "files_with_matches"));
        var caseInsensitive = FileToolArguments.OptionalBool(arguments, "-i", fallback: false);
        var headLimit = FileToolArguments.OptionalInt(
            arguments,
            "head_limit",
            DefaultHeadLimit,
            minimum: 0,
            maximum: MaximumHeadLimit);
        var offset = FileToolArguments.OptionalInt(
            arguments,
            "offset",
            fallback: 0,
            minimum: 0,
            maximum: int.MaxValue);
        var path = fileSystem.ResolvePath(requestedPath);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("Search path was not found.", fileSystem.DisplayPath(path));

        var regexOptions = RegexOptions.CultureInvariant;
        if (caseInsensitive)
            regexOptions |= RegexOptions.IgnoreCase;
        var regex = new Regex(pattern, regexOptions, RegexTimeout);
        var root = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var matcher = string.IsNullOrWhiteSpace(glob)
            ? null
            : FileSearch.CreateMatcher(glob, matchFilenameAtAnyDepth: true);

        var result = new StringBuilder();
        var scannedFiles = 0;
        var skippedFiles = 0;
        var matchingFiles = 0;
        var totalOccurrences = 0;
        var seenEntries = 0;
        var returnedEntries = 0;
        var entryLimitReached = false;
        var outputLimitReached = false;

        foreach (var file in EnumerateCandidates(path, root, matcher, ct))
        {
            scannedFiles++;
            var fileOccurrences = 0;
            var fileHadMatch = false;

            try
            {
                await using var reader = Utf8TextFile.OpenLineReader(file);
                var lineNumber = 0;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    lineNumber++;
                    if (line.Content.Contains('\0'))
                    {
                        skippedFiles++;
                        break;
                    }

                    var matches = regex.Matches(line.Content);
                    if (matches.Count == 0)
                        continue;

                    fileHadMatch = true;
                    fileOccurrences += matches.Count;
                    totalOccurrences += matches.Count;

                    if (outputMode != GrepOutputMode.Content)
                        continue;

                    if (!TryAppendEntry(
                            FormatContentMatch(file, lineNumber, matches[0].Index, matches[0].Length, line.Content),
                            offset,
                            headLimit,
                            ref seenEntries,
                            ref returnedEntries,
                            result,
                            out entryLimitReached,
                            out outputLimitReached))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is DecoderFallbackException or InvalidDataException)
            {
                skippedFiles++;
                continue;
            }

            if (fileHadMatch)
            {
                matchingFiles++;
                if (outputMode == GrepOutputMode.FilesWithMatches)
                {
                    TryAppendEntry(
                        fileSystem.DisplayPath(file),
                        offset,
                        headLimit,
                        ref seenEntries,
                        ref returnedEntries,
                        result,
                        out entryLimitReached,
                        out outputLimitReached);
                }
                else if (outputMode == GrepOutputMode.Count)
                {
                    TryAppendEntry(
                        $"{fileSystem.DisplayPath(file)}:{fileOccurrences}",
                        offset,
                        headLimit,
                        ref seenEntries,
                        ref returnedEntries,
                        result,
                        out entryLimitReached,
                        out outputLimitReached);
                }
            }

            if (entryLimitReached || outputLimitReached)
                break;
        }

        var truncation = entryLimitReached
            ? $" Results limited to head_limit={headLimit} after offset={offset}."
            : outputLimitReached
                ? $" Output limited to {MaximumOutputCharacters:N0} characters."
                : string.Empty;
        var skipped = skippedFiles == 0
            ? string.Empty
            : $"\nSkipped binary or non-UTF-8 files: {skippedFiles:N0}";
        var body = result.Length == 0 ? "No matches found." : result.ToString().TrimEnd();

        yield return new ToolOutput.Result(
            $"Mode: {FormatOutputMode(outputMode)}\n" +
            $"Matching files: {matchingFiles:N0}\n" +
            $"Occurrences observed: {totalOccurrences:N0}\n" +
            $"Entries returned: {returnedEntries:N0}.{truncation}\n" +
            $"Files scanned: {scannedFiles:N0}{skipped}\n\n{body}");
    }

    private IEnumerable<string> EnumerateCandidates(
        string path,
        string root,
        Matcher? matcher,
        CancellationToken ct)
    {
        if (File.Exists(path))
        {
            yield return path;
            yield break;
        }

        foreach (var file in FileSearch.EnumerateFiles(path, ct))
        {
            if (matcher is null || FileSearch.IsMatch(matcher, root, file))
                yield return file;
        }
    }

    private bool TryAppendEntry(
        string entry,
        int offset,
        int headLimit,
        ref int seenEntries,
        ref int returnedEntries,
        StringBuilder output,
        out bool entryLimitReached,
        out bool outputLimitReached)
    {
        entryLimitReached = false;
        outputLimitReached = false;

        if (seenEntries++ < offset)
            return true;
        if (headLimit != 0 && returnedEntries >= headLimit)
        {
            entryLimitReached = true;
            return false;
        }

        var required = entry.Length + (output.Length == 0 ? 0 : 1);
        if (output.Length + required > MaximumOutputCharacters)
        {
            outputLimitReached = true;
            return false;
        }

        if (output.Length > 0)
            output.AppendLine();
        output.Append(entry);
        returnedEntries++;
        return true;
    }

    private string FormatContentMatch(
        string file,
        int lineNumber,
        int matchIndex,
        int matchLength,
        string line)
    {
        var previewStart = Math.Max(0, matchIndex - MaximumPreviewCharacters / 3);
        var previewLength = Math.Min(MaximumPreviewCharacters, line.Length - previewStart);

        if (matchLength > MaximumPreviewCharacters)
        {
            previewStart = matchIndex;
            previewLength = MaximumPreviewCharacters;
        }
        else if (matchIndex + matchLength > previewStart + previewLength)
        {
            previewStart = Math.Max(0, matchIndex + matchLength - MaximumPreviewCharacters);
            previewLength = Math.Min(MaximumPreviewCharacters, line.Length - previewStart);
        }

        var prefix = previewStart == 0 ? string.Empty : "…";
        var suffix = previewStart + previewLength == line.Length ? string.Empty : "…";
        var preview = line.Substring(previewStart, previewLength);
        return $"{fileSystem.DisplayPath(file)}:{lineNumber}:{matchIndex + 1}: " +
            $"{prefix}{preview}{suffix}";
    }

    private static GrepOutputMode ParseOutputMode(string value) => value switch
    {
        "content" => GrepOutputMode.Content,
        "files_with_matches" => GrepOutputMode.FilesWithMatches,
        "count" => GrepOutputMode.Count,
        _ => throw new ArgumentException(
            "Argument 'output_mode' must be content, files_with_matches, or count."),
    };

    private static string FormatOutputMode(GrepOutputMode value) => value switch
    {
        GrepOutputMode.Content => "content",
        GrepOutputMode.FilesWithMatches => "files_with_matches",
        GrepOutputMode.Count => "count",
        _ => throw new UnreachableException(),
    };

    private enum GrepOutputMode
    {
        Content,
        FilesWithMatches,
        Count,
    }
}
