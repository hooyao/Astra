using Microsoft.Extensions.FileSystemGlobbing;

namespace Astra.Core.Files;

internal static class FileSearch
{
    private const int MaximumBraceExpansions = 64;
    private static readonly HashSet<string> VersionControlDirectories = new(
        [".git", ".svn", ".hg", ".bzr", ".jj", ".sl"],
        StringComparer.OrdinalIgnoreCase);

    public static Matcher CreateMatcher(string pattern, bool matchFilenameAtAnyDepth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var matcher = new Matcher(comparison);
        foreach (var expanded in ExpandBraces(pattern.Replace('\\', '/')))
        {
            var normalized = matchFilenameAtAnyDepth && !expanded.Contains('/')
                ? $"**/{expanded}"
                : expanded;
            matcher.AddInclude(normalized);
        }

        return matcher;
    }

    public static bool IsMatch(Matcher matcher, string root, string file)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        return matcher.Match(relative).HasMatches;
    }

    public static IEnumerable<string> EnumerateFiles(
        string root,
        CancellationToken ct)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = directories.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(entries, StringComparerFromPlatform());
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries[i];
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!VersionControlDirectories.Contains(Path.GetFileName(entry)))
                        directories.Push(entry);
                    continue;
                }

                yield return entry;
            }
        }
    }

    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var pending = new Queue<string>();
        pending.Enqueue(pattern);
        var expansions = 0;

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            var open = current.IndexOf('{', StringComparison.Ordinal);
            if (open < 0)
            {
                yield return current;
                continue;
            }

            var close = current.IndexOf('}', open + 1);
            if (close < 0)
                throw new ArgumentException("Glob pattern contains an unmatched '{'.", nameof(pattern));

            var alternatives = current[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (alternatives.Length == 0)
                throw new ArgumentException("Glob brace expression must contain an alternative.", nameof(pattern));

            foreach (var alternative in alternatives)
            {
                expansions++;
                if (expansions > MaximumBraceExpansions)
                    throw new ArgumentException("Glob pattern expands to too many alternatives.", nameof(pattern));
                pending.Enqueue(current[..open] + alternative + current[(close + 1)..]);
            }
        }
    }

    private static StringComparer StringComparerFromPlatform() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
