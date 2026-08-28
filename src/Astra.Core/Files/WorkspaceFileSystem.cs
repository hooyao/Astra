using System.Text;

namespace Astra.Core.Files;

/// <summary>
/// Resolves file-tool paths from one working directory, with an optional set of
/// hard allowed roots. With no allowed roots, absolute paths may address any
/// local directory. The restricted mode is adapted from Microsoft Semantic
/// Kernel's FileIOPlugin: canonicalize before access and reject link traversal
/// outside every configured root.
/// </summary>
public sealed class WorkspaceFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);
    private readonly string _declaredBase;
    private readonly string _physicalBase;
    private readonly RootBoundary[] _allowedRoots;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public WorkspaceFileSystem(
        string baseDirectory,
        IEnumerable<string>? allowedRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        _declaredBase = NormalizeExistingDirectory(baseDirectory);
        _physicalBase = Path.TrimEndingDirectorySeparator(
            ResolveExistingLinks(_declaredBase, finalIsDirectory: true));

        _allowedRoots = (allowedRoots ?? [])
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root =>
            {
                var declared = NormalizeExistingDirectory(root);
                var physical = Path.TrimEndingDirectorySeparator(
                    ResolveExistingLinks(declared, finalIsDirectory: true));
                return new RootBoundary(declared, physical);
            })
            .DistinctBy(root => root.Declared, StringComparerFromPlatform())
            .ToArray();
    }

    /// <summary>Base used to resolve relative paths; not necessarily a boundary.</summary>
    public string BaseDirectory => _declaredBase;

    /// <summary>Compatibility name retained for tool descriptions.</summary>
    public string RootPath => BaseDirectory;

    public bool IsRestricted => _allowedRoots.Length > 0;

    public IReadOnlyList<string> AllowedRoots => _allowedRoots
        .Select(root => root.Declared)
        .ToArray();

    public string AccessDescription => IsRestricted
        ? $"restricted to: {string.Join(", ", AllowedRoots)}"
        : $"unrestricted local paths; relative paths resolve from: {BaseDirectory}";

    /// <summary>Resolve a path and enforce allowed roots only when configured.</summary>
    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RejectDeviceOrNetworkPath(path);

        var lexicalPath = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, _declaredBase);

        if (IsRestricted)
            EnsureAllowed(lexicalPath, path, physical: false);

        var physicalPath = ResolveExistingLinks(lexicalPath, finalIsDirectory: false);
        if (IsRestricted)
            EnsureAllowed(physicalPath, path, physical: true);
        return physicalPath;
    }

    public string DisplayPath(string resolvedPath)
    {
        if (!IsContained(_physicalBase, resolvedPath))
            return resolvedPath;

        var relative = Path.GetRelativePath(_physicalBase, resolvedPath);
        return relative == "." ? Path.GetFileName(resolvedPath) : relative;
    }

    public async Task WriteTextAtomicallyAsync(
        string resolvedPath,
        string content,
        bool overwrite,
        CancellationToken ct,
        bool emitUtf8Bom = false,
        bool createParentDirectories = false)
    {
        var directory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrEmpty(directory))
            throw new DirectoryNotFoundException($"Parent directory does not exist: {directory}");
        if (createParentDirectories)
            Directory.CreateDirectory(directory);
        else if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Parent directory does not exist: {directory}");
        if (!overwrite && File.Exists(resolvedPath))
            throw new IOException($"File already exists: {DisplayPath(resolvedPath)}.");

        UnixFileMode? existingUnixMode = null;
        if (!OperatingSystem.IsWindows() && File.Exists(resolvedPath))
            existingUnixMode = File.GetUnixFileMode(resolvedPath);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(resolvedPath)}.{Guid.NewGuid():N}.astra.tmp");

        try
        {
            var encoding = emitUtf8Bom ? Utf8WithBom : Utf8NoBom;
            await File.WriteAllTextAsync(temporaryPath, content, encoding, ct);
            if (!OperatingSystem.IsWindows() && existingUnixMode is { } unixMode)
                File.SetUnixFileMode(temporaryPath, unixMode);
            File.Move(temporaryPath, resolvedPath, overwrite);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The primary operation already has the useful failure. A leaked
                // temp file is preferable to masking it with cleanup failure.
            }
            catch (UnauthorizedAccessException)
            {
                // Same cleanup rule as IOException above.
            }
        }
    }

    private void EnsureAllowed(string candidate, string originalInput, bool physical)
    {
        if (_allowedRoots.Any(root =>
                IsContained(physical ? root.Physical : root.Declared, candidate)))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"Path is outside the configured workspace roots: {originalInput}");
    }

    private bool IsContained(string root, string candidate)
    {
        if (string.Equals(root, candidate, _pathComparison))
            return true;
        var rootedPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootedPrefix, _pathComparison);
    }

    private static string NormalizeExistingDirectory(string path)
    {
        RejectDeviceOrNetworkPath(path);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory does not exist: {fullPath}");
        return fullPath;
    }

    private static IEqualityComparer<string> StringComparerFromPlatform() =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string ResolveExistingLinks(string fullPath, bool finalIsDirectory)
    {
        var pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
            throw new ArgumentException("A fully qualified path is required.", nameof(fullPath));

        var current = pathRoot;
        var remainder = fullPath[pathRoot.Length..];
        var parts = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            FileSystemInfo info = i == parts.Length - 1 && !finalIsDirectory
                ? new FileInfo(next)
                : new DirectoryInfo(next);

            string? linkTarget;
            try
            {
                linkTarget = info.LinkTarget;
            }
            catch (IOException)
            {
                linkTarget = null;
            }

            if (linkTarget is null)
            {
                current = next;
                continue;
            }

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null)
                throw new UnauthorizedAccessException($"Dangling symbolic link is not allowed: {next}");

            current = resolved.FullName;
        }

        return Path.GetFullPath(current);
    }

    private static void RejectDeviceOrNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) ||
            path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("UNC and device paths are not supported.", nameof(path));
        }
    }

    private sealed record RootBoundary(string Declared, string Physical);
}
