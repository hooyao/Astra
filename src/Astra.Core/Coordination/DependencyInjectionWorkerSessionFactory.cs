using Microsoft.Extensions.DependencyInjection;

namespace Astra.Core.Coordination;

/// <summary>
/// Creates one independent dependency-injection scope per worker execution.
/// The scope is created only after the coordinator admits the worker and is
/// disposed before its terminal completion is published.
/// </summary>
public sealed class DependencyInjectionWorkerSessionFactory(
    IServiceScopeFactory scopeFactory) : IWorkerSessionFactory
{
    public async ValueTask<IWorkerSession> CreateAsync(
        WorkerTaskId taskId,
        WorkerId workerId,
        WorkerRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var worker = scope.ServiceProvider.GetRequiredService<IWorker>();
            return new ScopedWorkerSession(scope, worker, taskId, workerId, request);
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class ScopedWorkerSession(
        AsyncServiceScope scope,
        IWorker worker,
        WorkerTaskId taskId,
        WorkerId workerId,
        WorkerRequest request) : IWorkerSession
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _lifetimeCancellation = new();
        private CancellationTokenSource? _executionCancellation;
        private Task<WorkerCompletion>? _execution;
        private bool _started;
        private bool _disposed;

        public Task<WorkerCompletion> RunAsync(CancellationToken ct)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_started)
                    throw new InvalidOperationException("A worker session can run only once.");

                _started = true;
                _executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    ct,
                    _lifetimeCancellation.Token);
                _execution = worker.RunAsync(
                    taskId,
                    workerId,
                    request,
                    _executionCancellation.Token);
                return _execution;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task<WorkerCompletion>? execution;
            CancellationTokenSource? executionCancellation;
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
                execution = _execution;
                executionCancellation = _executionCancellation;
            }

            try
            {
                try
                {
                    await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
                }
                catch
                {
                    // A cancellation callback must not skip joining execution
                    // or release the scope while the worker is still running.
                }

                if (execution is not null)
                {
                    try
                    {
                        await execution.ConfigureAwait(false);
                    }
                    catch
                    {
                        // RunAsync owns execution outcome propagation. Disposal
                        // only guarantees cancellation, joining, and cleanup.
                    }
                }
            }
            finally
            {
                executionCancellation?.Dispose();
                _lifetimeCancellation.Dispose();
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
