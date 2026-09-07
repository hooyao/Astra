using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Astra.Core.Coordination;

/// <summary>Starts one clean-context worker and returns immediately.</summary>
public sealed class AgentTool(WorkerCoordinator coordinator) : IToolExecutor
{
    public const string ToolName = "Agent";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "description": { "type": "string", "description": "Short task label for progress and completion messages." },
            "prompt": { "type": "string", "description": "Complete standalone task, required evidence, file ownership, constraints, and done criteria." },
            "access_mode": { "type": "string", "enum": ["read_only", "write"], "default": "read_only", "description": "Use write only when the worker must modify files; existing files must be read before editing." }
          },
          "required": ["description", "prompt"],
          "additionalProperties": false
        }
        """);

    public static ToolDefinition Definition { get; } = new(
        ToolName,
        "Start an isolated worker for a substantial independent task. The default access_mode is read_only; " +
        "write mode requires permission and should own a non-overlapping file set. " +
        "The worker cannot see this conversation, so prompt must be self-contained. " +
        "For independent work, emit multiple Agent calls in the same response so they run in parallel. " +
        "Completions arrive later as task-notification user messages; do not poll.",
        Schema,
        static arguments => ParseAccessMode(arguments) switch
        {
            WorkerAccessMode.ReadOnly => ToolAction.Read,
            WorkerAccessMode.Write => ToolAction.Write,
            _ => ToolAction.Other,
        });

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var description = RequireString(arguments, "description");
        var prompt = RequireString(arguments, "prompt");
        var accessMode = ParseAccessMode(arguments) ??
            throw new ArgumentException("Argument 'access_mode' must be 'read_only' or 'write'.");
        var result = coordinator.Start(new WorkerRequest(description, prompt, accessMode), ct);

        yield return result switch
        {
            WorkerStartResult.Started started => new ToolOutput.Result(
                $"Worker started.\ntask_id: {started.Handle.TaskId}\n" +
                $"worker_id: {started.Handle.WorkerId}\n" +
                "Its completion will arrive automatically; do not poll."),
            WorkerStartResult.Rejected rejected => new ToolOutput.Result(
                $"Worker was not started: {rejected.Reason}"),
            _ => throw new UnreachableException(),
        };

        await Task.CompletedTask;
    }

    private static WorkerAccessMode? ParseAccessMode(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("access_mode", out var raw) || raw is null)
            return WorkerAccessMode.ReadOnly;

        var value = raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null,
        };
        return value switch
        {
            "read_only" => WorkerAccessMode.ReadOnly,
            "write" => WorkerAccessMode.Write,
            _ => null,
        };
    }

    private static string RequireString(
        IDictionary<string, object?>? arguments,
        string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var raw) || raw is null)
            throw new ArgumentException($"Missing required '{name}' argument.");

        var value = raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");
        return value;
    }
}
