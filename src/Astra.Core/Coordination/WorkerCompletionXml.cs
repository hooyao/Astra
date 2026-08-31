using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.Extensions.AI;

namespace Astra.Core.Coordination;

public static class WorkerCompletionXml
{
    public static ChatMessage ToUserMessage(IReadOnlyList<WorkerCompletion> completions) =>
        new(ChatRole.User, Serialize(completions));

    public static string Serialize(IReadOnlyList<WorkerCompletion> completions)
    {
        ArgumentNullException.ThrowIfNull(completions);
        if (completions.Count == 0)
            throw new ArgumentException("At least one worker completion is required.", nameof(completions));

        var output = new StringBuilder();
        using var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            Indent = true,
            NewLineChars = "\n",
        });

        foreach (var completion in completions)
            WriteCompletion(writer, completion);
        writer.Flush();
        return output.ToString();
    }

    private static void WriteCompletion(XmlWriter writer, WorkerCompletion completion)
    {
        writer.WriteStartElement("task-notification");
        writer.WriteElementString("task-id", completion.TaskId.Value);
        writer.WriteElementString("worker-id", completion.WorkerId.Value);
        writer.WriteElementString("status", FormatStatus(completion.Status));
        writer.WriteElementString("summary", $"Worker \"{completion.Description}\" {FormatStatus(completion.Status)}");

        if (completion.Report is { } report)
            WriteReport(writer, report);
        if (completion.Failure is { } failure)
            WriteFailure(writer, failure);
        WriteUsage(writer, completion.Usage);
        WriteChangedPaths(writer, completion.ChangedPaths);
        WriteArtifacts(writer, completion.Artifacts);
        writer.WriteEndElement();
    }

    private static void WriteReport(XmlWriter writer, WorkerReport report)
    {
        writer.WriteStartElement("result");
        writer.WriteElementString("summary", report.Summary);

        writer.WriteStartElement("findings");
        foreach (var finding in report.Findings)
        {
            writer.WriteStartElement("finding");
            writer.WriteElementString("claim", finding.Claim);
            writer.WriteStartElement("evidence");
            foreach (var evidence in finding.Evidence)
            {
                writer.WriteStartElement("item");
                writer.WriteElementString("reference", evidence.Reference);
                if (evidence.Excerpt is not null)
                    writer.WriteElementString("excerpt", evidence.Excerpt);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("changes");
        foreach (var change in report.Changes)
        {
            writer.WriteStartElement("change");
            writer.WriteElementString("path", change.Path);
            writer.WriteElementString("description", change.Description);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        writer.WriteStartElement("verification");
        foreach (var verification in report.Verification)
        {
            writer.WriteStartElement("check");
            writer.WriteElementString("command", verification.Check);
            if (verification.ExitCode is { } exitCode)
            {
                writer.WriteElementString(
                    "exit-code",
                    exitCode.ToString(CultureInfo.InvariantCulture));
            }
            writer.WriteElementString("result", verification.Result);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();

        WriteStrings(writer, "risks", "risk", report.Risks);
        WriteStrings(writer, "open-questions", "question", report.OpenQuestions);
        writer.WriteEndElement();
    }

    private static void WriteFailure(XmlWriter writer, WorkerFailure failure)
    {
        writer.WriteStartElement("failure");
        writer.WriteElementString("code", failure.Code);
        writer.WriteElementString("message", failure.Message);
        writer.WriteElementString("retryable", failure.Retryable ? "true" : "false");
        writer.WriteEndElement();
    }

    private static void WriteUsage(XmlWriter writer, WorkerUsage usage)
    {
        writer.WriteStartElement("usage");
        WriteNumber(writer, "input-tokens", usage.InputTokens);
        WriteNumber(writer, "output-tokens", usage.OutputTokens);
        WriteNumber(writer, "cached-input-tokens", usage.CachedInputTokens);
        WriteNumber(writer, "reasoning-tokens", usage.ReasoningTokens);
        WriteNumber(writer, "total-tokens", usage.TotalTokens);
        WriteNumber(writer, "model-calls", usage.ModelCalls);
        WriteNumber(writer, "tool-calls", usage.ToolCalls);
        WriteNumber(writer, "duration-ms", usage.DurationMilliseconds);
        foreach (var (name, count) in usage.AdditionalTokenCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.WriteStartElement("additional-count");
            writer.WriteAttributeString("name", name);
            writer.WriteString(count.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteChangedPaths(XmlWriter writer, IReadOnlyList<string> paths) =>
        WriteStrings(writer, "changed-paths", "path", paths);

    private static void WriteArtifacts(XmlWriter writer, IReadOnlyList<WorkerArtifact> artifacts)
    {
        writer.WriteStartElement("artifacts");
        foreach (var artifact in artifacts)
        {
            writer.WriteStartElement("artifact");
            writer.WriteAttributeString("kind", artifact.Kind);
            writer.WriteString(artifact.Location);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void WriteStrings(
        XmlWriter writer,
        string container,
        string item,
        IReadOnlyList<string> values)
    {
        writer.WriteStartElement(container);
        foreach (var value in values)
            writer.WriteElementString(item, value);
        writer.WriteEndElement();
    }

    private static void WriteNumber(XmlWriter writer, string name, long value) =>
        writer.WriteElementString(name, value.ToString(CultureInfo.InvariantCulture));

    private static string FormatStatus(WorkerStatus status) => status switch
    {
        WorkerStatus.Completed => "completed",
        WorkerStatus.Failed => "failed",
        WorkerStatus.Cancelled => "cancelled",
        WorkerStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
