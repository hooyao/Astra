using Astra.Core.Compaction;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Core.Coordination;

/// <summary>
/// Runs one worker request through the AgentLoop owned by the current worker
/// execution scope.
/// </summary>
public sealed class AgentLoopWorker(
    [FromKeyedServices(AgentServiceKeys.WorkerLoop)] AgentLoop loop,
    UsageTrackingChatClient trackingClient,
    IChatTokenEstimator tokenEstimator,
    TimeProvider timeProvider) : IWorker
{
    public async Task<WorkerCompletion> RunAsync(
        WorkerTaskId taskId,
        WorkerId workerId,
        WorkerRequest request,
        CancellationToken ct)
    {
        var started = timeProvider.GetTimestamp();
        var toolCalls = 0;

        try
        {
            Validate(request);
            var prompt = WorkerReportProtocol.AddInstructions(request.Prompt, request.MaxReportTokens);

            await foreach (var evt in loop.SubmitAsync(
                               prompt,
                               new AgentTurnOptions { MaxOutputTokens = request.MaxReportTokens },
                               ct))
            {
                if (evt is AgentEvent.ToolUse)
                    toolCalls++;
            }

            var duration = ElapsedMilliseconds(started);
            var usage = trackingClient.Snapshot(toolCalls, duration);
            if (!WorkerReportProtocol.TryParse(
                    trackingClient.LastResponseText,
                    request.MaxReportTokens,
                    tokenEstimator,
                    out var report,
                    out var failure))
            {
                return Failed(taskId, workerId, request, usage, failure!);
            }

            return new WorkerCompletion(
                taskId,
                workerId,
                request.Description,
                WorkerStatus.Completed,
                report,
                usage,
                Failure: null,
                ChangedPaths: [],
                Artifacts: []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new WorkerCompletion(
                taskId,
                workerId,
                request.Description,
                WorkerStatus.Cancelled,
                Report: null,
                trackingClient.Snapshot(toolCalls, ElapsedMilliseconds(started)),
                Failure: null,
                ChangedPaths: [],
                Artifacts: []);
        }
        catch (Exception ex)
        {
            return Failed(
                taskId,
                workerId,
                request,
                trackingClient.Snapshot(toolCalls, ElapsedMilliseconds(started)),
                new WorkerFailure(
                    "worker_execution_failed",
                    BoundFailureMessage(ex.Message),
                    Retryable: false));
        }
    }

    private static void Validate(WorkerRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.MaxReportTokens is < 128 or > 4_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "MaxReportTokens must be between 128 and 4,000.");
        }
    }

    private static WorkerCompletion Failed(
        WorkerTaskId taskId,
        WorkerId workerId,
        WorkerRequest request,
        WorkerUsage usage,
        WorkerFailure failure) =>
        new(
            taskId,
            workerId,
            request.Description,
            WorkerStatus.Failed,
            Report: null,
            usage,
            failure,
            ChangedPaths: [],
            Artifacts: []);

    private long ElapsedMilliseconds(long started) =>
        (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;

    private static string BoundFailureMessage(string message)
    {
        const int maximumCharacters = 1_000;
        return message.Length <= maximumCharacters
            ? message
            : message[..maximumCharacters] + " [truncated]";
    }
}
