using System.Text.Json;
using Astra.Core.Compaction;
using Microsoft.Extensions.AI;

namespace Astra.Core.Coordination;

internal static class WorkerReportProtocol
{
    public static string AddInstructions(string prompt, int maxReportTokens) =>
        $$"""
        {{prompt}}

        <worker-report-contract>
        When the task is complete, your final response must be only one JSON object.
        Do not wrap it in Markdown. Keep the complete object within {{maxReportTokens:N0}} tokens.
        Use this exact shape:
        {
          "summary": "outcome and central conclusion",
          "findings": [
            {
              "claim": "specific claim",
              "evidence": [{ "reference": "file:line or other durable reference", "excerpt": "short optional excerpt" }]
            }
          ],
          "changes": [{ "path": "changed path", "description": "semantic change" }],
          "verification": [{ "check": "exact command or check", "exit_code": 0, "result": "meaningful result" }],
          "risks": ["known uncertainty or unverified area"],
          "open_questions": ["information still required"]
        }
        Use empty arrays for sections that do not apply. Do not include raw tool transcripts,
        complete files, or intermediate reasoning.
        </worker-report-contract>
        """;

    public static bool TryParse(
        string text,
        int maxReportTokens,
        IChatTokenEstimator tokenEstimator,
        out WorkerReport? report,
        out WorkerFailure? failure)
    {
        report = null;
        failure = null;

        var json = RemoveMarkdownFence(text.Trim());
        var estimatedTokens = tokenEstimator.EstimateTokens(
            [new ChatMessage(ChatRole.Assistant, json)]);
        if (estimatedTokens > maxReportTokens)
        {
            failure = new WorkerFailure(
                "report_too_large",
                $"Worker report is approximately {estimatedTokens:N0} tokens; maximum is {maxReportTokens:N0}.",
                Retryable: true);
            return false;
        }

        try
        {
            report = JsonSerializer.Deserialize(
                json,
                WorkerReportJsonContext.Default.WorkerReport);
        }
        catch (JsonException ex)
        {
            failure = new WorkerFailure(
                "invalid_worker_report",
                "Worker final response is not valid report JSON " +
                $"(line {ex.LineNumber ?? 0:N0}, byte {ex.BytePositionInLine ?? 0:N0}).",
                Retryable: true);
            return false;
        }

        if (report is null ||
            string.IsNullOrWhiteSpace(report.Summary) ||
            report.Findings is null ||
            report.Changes is null ||
            report.Verification is null ||
            report.Risks is null ||
            report.OpenQuestions is null)
        {
            report = null;
            failure = new WorkerFailure(
                "invalid_worker_report",
                "Worker report is missing a non-empty summary or one of the required arrays.",
                Retryable: true);
            return false;
        }

        if (report.Findings.Any(finding =>
                finding is null ||
                string.IsNullOrWhiteSpace(finding.Claim) ||
                finding.Evidence is null ||
                finding.Evidence.Any(evidence =>
                    evidence is null || string.IsNullOrWhiteSpace(evidence.Reference))) ||
            report.Changes.Any(change =>
                change is null ||
                string.IsNullOrWhiteSpace(change.Path) ||
                string.IsNullOrWhiteSpace(change.Description)) ||
            report.Verification.Any(verification =>
                verification is null ||
                string.IsNullOrWhiteSpace(verification.Check) ||
                string.IsNullOrWhiteSpace(verification.Result)) ||
            report.Risks.Any(string.IsNullOrWhiteSpace) ||
            report.OpenQuestions.Any(string.IsNullOrWhiteSpace))
        {
            report = null;
            failure = new WorkerFailure(
                "invalid_worker_report",
                "Worker report contains an empty required field or invalid array item.",
                Retryable: true);
            return false;
        }

        return true;
    }

    private static string RemoveMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstLineEnd = text.IndexOf('\n');
        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && closingFence > firstLineEnd
            ? text[(firstLineEnd + 1)..closingFence].Trim()
            : text;
    }
}
