using System.Collections.Concurrent;

namespace Astra.Core.Files;

/// <summary>
/// Agent-scope record of the exact file content most recently observed or
/// successfully written through Astra's file tools.
/// </summary>
public sealed class FileObservationStore
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private readonly ConcurrentDictionary<string, FileContentVersion> _versions = new(PathComparer);
    private readonly ConcurrentDictionary<string, byte> _changedPaths = new(PathComparer);

    internal bool TryGet(string resolvedPath, out FileContentVersion version) =>
        _versions.TryGetValue(resolvedPath, out version);

    internal void Record(string resolvedPath, FileContentVersion version) =>
        _versions[resolvedPath] = version;

    internal void RecordWrite(string resolvedPath, FileContentVersion version)
    {
        _versions[resolvedPath] = version;
        _changedPaths[resolvedPath] = 0;
    }

    internal void Invalidate(string resolvedPath) =>
        _versions.TryRemove(resolvedPath, out _);

    public IReadOnlyList<string> SnapshotChangedPaths() =>
        _changedPaths.Keys.Order(PathComparer).ToArray();
}

internal readonly record struct FileContentVersion(string Sha256);
