namespace Astra.Core.Coordination;

/// <summary>
/// Executes one worker inside the dependency-injection scope that owns all of
/// its private runtime state.
/// </summary>
public interface IWorker
{
    Task<WorkerCompletion> RunAsync(
        WorkerTaskId taskId,
        WorkerId workerId,
        WorkerRequest request,
        CancellationToken ct);
}

/// <summary>Creates a one-shot, independently owned worker execution session.</summary>
public interface IWorkerSessionFactory
{
    ValueTask<IWorkerSession> CreateAsync(
        WorkerTaskId taskId,
        WorkerId workerId,
        WorkerRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Owns one worker execution and the scope containing its AgentLoop, provider
/// client, telemetry, private history, and other scoped dependencies. Disposal
/// cancels and joins an active execution before releasing the scope.
/// </summary>
public interface IWorkerSession : IAsyncDisposable
{
    Task<WorkerCompletion> RunAsync(CancellationToken ct);
}
