using System.Text.Json.Serialization;

namespace Astra.Core.Coordination;

public readonly record struct WorkerId
{
    public WorkerId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static WorkerId New() => new($"worker-{Guid.NewGuid():N}");

    public override string ToString() => Value;
}

public readonly record struct WorkerTaskId
{
    public WorkerTaskId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static WorkerTaskId New() => new($"task-{Guid.NewGuid():N}");

    public override string ToString() => Value;
}

public enum WorkerAccessMode
{
    ReadOnly,
    Write,
}

public enum WorkerStatus
{
    Completed,
    Failed,
    Cancelled,
    Blocked,
}

public sealed record WorkerRequest(
    string Description,
    string Prompt,
    WorkerAccessMode AccessMode = WorkerAccessMode.ReadOnly,
    int MaxReportTokens = 2_000);

public sealed record WorkerReport(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("findings")] WorkerFinding[] Findings,
    [property: JsonPropertyName("changes")] WorkerChange[] Changes,
    [property: JsonPropertyName("verification")] WorkerVerification[] Verification,
    [property: JsonPropertyName("risks")] string[] Risks,
    [property: JsonPropertyName("open_questions")] string[] OpenQuestions);

public sealed record WorkerFinding(
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("evidence")] WorkerEvidence[] Evidence);

public sealed record WorkerEvidence(
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("excerpt")] string? Excerpt = null);

public sealed record WorkerChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string Description);

public sealed record WorkerVerification(
    [property: JsonPropertyName("check")] string Check,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("result")] string Result);

public sealed record WorkerFailure(string Code, string Message, bool Retryable);

public sealed record WorkerArtifact(string Kind, string Location);

public sealed record WorkerUsage(
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    long TotalTokens,
    int ModelCalls,
    int ToolCalls,
    long DurationMilliseconds,
    IReadOnlyDictionary<string, long> AdditionalTokenCounts);

public sealed record WorkerCompletion(
    WorkerTaskId TaskId,
    WorkerId WorkerId,
    string Description,
    WorkerStatus Status,
    WorkerReport? Report,
    WorkerUsage Usage,
    WorkerFailure? Failure,
    IReadOnlyList<string> ChangedPaths,
    IReadOnlyList<WorkerArtifact> Artifacts);
