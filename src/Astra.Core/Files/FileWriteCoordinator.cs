using System.Collections.Concurrent;

namespace Astra.Core.Files;

/// <summary>
/// Process-local same-path write serialization shared by all agent scopes.
/// Different paths use different gates; external processes do not participate.
/// </summary>
public sealed class FileWriteCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async ValueTask<IDisposable> AcquireAsync(
        string resolvedPath,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);
        var gate = _gates.GetOrAdd(resolvedPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() =>
            Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
