using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Astra.Core.Coordination;

public sealed class WorkerHandle(
    WorkerTaskId taskId,
    WorkerId workerId,
    Task<WorkerCompletion> completion)
{
    public WorkerTaskId TaskId => taskId;
    public WorkerId WorkerId => workerId;
    public Task<WorkerCompletion> Completion => completion;
}

public abstract record WorkerStartResult
{
    private WorkerStartResult() { }

    public sealed record Started(WorkerHandle Handle) : WorkerStartResult;
    public sealed record Rejected(string Reason) : WorkerStartResult;
}

/// <summary>
/// Owns worker lifecycle, bounded parallelism, targeted cancellation, terminal
/// completion fan-in, and one global write lane. Every admitted worker gets an
/// independent dependency-injection scope through IWorkerSessionFactory.
/// </summary>
public sealed class WorkerCoordinator : IAsyncDisposable
{
    private readonly IWorkerSessionFactory _sessionFactory;
    private readonly Func<WorkerTaskId> _taskIdFactory;
    private readonly Func<WorkerId> _workerIdFactory;
    private readonly SemaphoreSlim _workerSlots;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly ConcurrentDictionary<WorkerId, Registration> _workers = new();
    private readonly object _lifecycleGate = new();
    private readonly Channel<WorkerCompletion> _completions = Channel.CreateUnbounded<WorkerCompletion>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private int _disposed;
    private int _pendingCompletions;

    public WorkerCoordinator(
        IWorkerSessionFactory sessionFactory,
        int maxConcurrentWorkers = 8,
        Func<WorkerTaskId>? taskIdFactory = null,
        Func<WorkerId>? workerIdFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        if (maxConcurrentWorkers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentWorkers));

        _sessionFactory = sessionFactory;
        _workerSlots = new SemaphoreSlim(maxConcurrentWorkers, maxConcurrentWorkers);
        _taskIdFactory = taskIdFactory ?? WorkerTaskId.New;
        _workerIdFactory = workerIdFactory ?? WorkerId.New;
    }

    public int ActiveCount => _workers.Count;
    public int PendingCompletionCount => Volatile.Read(ref _pendingCompletions);
    public bool HasOutstandingWork => ActiveCount > 0 || PendingCompletionCount > 0;

    public WorkerStartResult Start(
        WorkerRequest request,
        CancellationToken lifetimeToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Description))
            return new WorkerStartResult.Rejected("Worker description must not be blank.");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return new WorkerStartResult.Rejected("Worker prompt must not be blank.");
        if (request.MaxReportTokens is < 128 or > 4_000)
            return new WorkerStartResult.Rejected("Worker report limit must be between 128 and 4,000 tokens.");

        lock (_lifecycleGate)
        {
            if (_disposed != 0)
                return new WorkerStartResult.Rejected("Worker coordinator is shutting down.");

            var taskId = _taskIdFactory();
            var workerId = _workerIdFactory();
            var completionSource = new TaskCompletionSource<WorkerCompletion>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = new WorkerHandle(taskId, workerId, completionSource.Task);
            var registration = new Registration(
                request,
                handle,
                completionSource,
                CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken));

            if (!_workers.TryAdd(workerId, registration))
            {
                registration.Dispose();
                return new WorkerStartResult.Rejected($"Duplicate worker ID: {workerId}.");
            }

            // Start while holding the lifecycle gate so DisposeAsync cannot
            // snapshot registrations before this execution has been launched.
            _ = ExecuteAsync(registration);
            return new WorkerStartResult.Started(handle);
        }
    }

    public bool Stop(WorkerId workerId)
    {
        if (!_workers.TryGetValue(workerId, out var registration) ||
            registration.CompletionSource.Task.IsCompleted)
        {
            return false;
        }

        registration.Cancel();
        return true;
    }

    /// <summary>
    /// Wait for one terminal completion, allow a short coalescing window, then
    /// drain immediately available completions into one coordinator input.
    /// </summary>
    public async ValueTask<IReadOnlyList<WorkerCompletion>> ReadCompletionBatchAsync(
        TimeSpan coalescingWindow,
        int maximumBatchSize = 32,
        CancellationToken ct = default)
    {
        if (coalescingWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(coalescingWindow));
        if (maximumBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));

        if (!await _completions.Reader.WaitToReadAsync(ct))
            return [];
        if (!_completions.Reader.TryRead(out var first))
            return [];
        Interlocked.Decrement(ref _pendingCompletions);

        var batch = new List<WorkerCompletion>(Math.Min(maximumBatchSize, 8)) { first };
        DrainAvailable(batch, maximumBatchSize);

        if (batch.Count == 1 && coalescingWindow > TimeSpan.Zero)
        {
            await Task.Delay(coalescingWindow, ct);
            DrainAvailable(batch, maximumBatchSize);
        }

        return batch;
    }

    /// <summary>
    /// Collect completions until the coordinator becomes idle. The session
    /// runner calls this only after a coordinator model turn has ended, so no
    /// new Agent calls can join the group while it waits. The resulting
    /// notifications can be synthesized in one model request.
    /// </summary>
    public async ValueTask<IReadOnlyList<WorkerCompletion>> ReadUntilIdleAsync(
        CancellationToken ct = default)
    {
        var results = new List<WorkerCompletion>();
        while (HasOutstandingWork)
        {
            if (_completions.Reader.TryRead(out var completion))
            {
                Interlocked.Decrement(ref _pendingCompletions);
                results.Add(completion);
                continue;
            }

            if (!await _completions.Reader.WaitToReadAsync(ct))
                break;
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        Registration[] registrations;
        lock (_lifecycleGate)
        {
            if (_disposed != 0)
                return;

            _disposed = 1;
            registrations = _workers.Values.ToArray();
        }

        foreach (var registration in registrations)
            registration.Cancel();

        await Task.WhenAll(registrations.Select(registration => registration.CompletionSource.Task));
        _completions.Writer.TryComplete();
        _workerSlots.Dispose();
        _writerGate.Dispose();
    }

    private async Task ExecuteAsync(Registration registration)
    {
        var slotHeld = false;
        var writerHeld = false;
        WorkerCompletion completion;

        try
        {
            if (registration.Request.AccessMode == WorkerAccessMode.Write)
            {
                await _writerGate.WaitAsync(registration.Token);
                writerHeld = true;
            }

            await _workerSlots.WaitAsync(registration.Token);
            slotHeld = true;

            await using (var session = await _sessionFactory.CreateAsync(
                             registration.Handle.TaskId,
                             registration.Handle.WorkerId,
                             registration.Request,
                             registration.Token))
            {
                completion = await session.RunAsync(registration.Token);
            }

            if (completion.TaskId != registration.Handle.TaskId ||
                completion.WorkerId != registration.Handle.WorkerId)
            {
                completion = Failure(
                    registration,
                    "invalid_worker_identity",
                    "Worker runner returned a completion for a different task or worker.");
            }
            else if (registration.Request.AccessMode == WorkerAccessMode.ReadOnly &&
                     completion.ChangedPaths.Count > 0)
            {
                completion = Failure(
                    registration,
                    "read_only_worker_modified_files",
                    "A read-only worker reported file modifications.");
            }
        }
        catch (OperationCanceledException) when (registration.Token.IsCancellationRequested)
        {
            completion = Cancelled(registration);
        }
        catch (Exception ex)
        {
            completion = Failure(
                registration,
                "worker_session_failed",
                BoundFailureMessage(ex.Message));
        }
        finally
        {
            if (writerHeld)
                _writerGate.Release();
            if (slotHeld)
                _workerSlots.Release();
        }

        Interlocked.Increment(ref _pendingCompletions);
        _workers.TryRemove(registration.Handle.WorkerId, out _);
        if (!_completions.Writer.TryWrite(completion))
            Interlocked.Decrement(ref _pendingCompletions);
        registration.Dispose();
        registration.CompletionSource.TrySetResult(completion);
    }

    private void DrainAvailable(List<WorkerCompletion> batch, int maximumBatchSize)
    {
        while (batch.Count < maximumBatchSize && _completions.Reader.TryRead(out var completion))
        {
            Interlocked.Decrement(ref _pendingCompletions);
            batch.Add(completion);
        }
    }

    private static WorkerCompletion Cancelled(Registration registration) =>
        new(
            registration.Handle.TaskId,
            registration.Handle.WorkerId,
            registration.Request.Description,
            WorkerStatus.Cancelled,
            Report: null,
            EmptyUsage,
            Failure: null,
            ChangedPaths: [],
            Artifacts: []);

    private static WorkerCompletion Failure(
        Registration registration,
        string code,
        string message) =>
        new(
            registration.Handle.TaskId,
            registration.Handle.WorkerId,
            registration.Request.Description,
            WorkerStatus.Failed,
            Report: null,
            EmptyUsage,
            new WorkerFailure(code, message, Retryable: false),
            ChangedPaths: [],
            Artifacts: []);

    private static WorkerUsage EmptyUsage { get; } = new(
        InputTokens: 0,
        OutputTokens: 0,
        CachedInputTokens: 0,
        ReasoningTokens: 0,
        TotalTokens: 0,
        ModelCalls: 0,
        ToolCalls: 0,
        DurationMilliseconds: 0,
        AdditionalTokenCounts: new Dictionary<string, long>());

    private static string BoundFailureMessage(string message)
    {
        const int maximumCharacters = 1_000;
        return message.Length <= maximumCharacters
            ? message
            : message[..maximumCharacters] + " [truncated]";
    }

    private sealed class Registration : IDisposable
    {
        private CancellationTokenSource? _cancellation;

        public Registration(
            WorkerRequest request,
            WorkerHandle handle,
            TaskCompletionSource<WorkerCompletion> completionSource,
            CancellationTokenSource cancellation)
        {
            Request = request;
            Handle = handle;
            CompletionSource = completionSource;
            _cancellation = cancellation;
            Token = cancellation.Token;
        }

        public WorkerRequest Request { get; }
        public WorkerHandle Handle { get; }
        public TaskCompletionSource<WorkerCompletion> CompletionSource { get; }
        public CancellationToken Token { get; }

        public void Cancel()
        {
            var cancellation = Volatile.Read(ref _cancellation);
            if (cancellation is null)
                return;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released the source.
            }
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _cancellation, null)?.Dispose();
    }
}
