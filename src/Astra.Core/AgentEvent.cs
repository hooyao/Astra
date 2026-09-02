using Astra.Core.Compaction;

namespace Astra.Core;

/// <summary>
/// Events yielded by the agent loop. The consumer drives the loop by iterating.
/// </summary>
public abstract record AgentEvent
{
    private AgentEvent() { }

    /// <summary>A streaming text chunk from the LLM.</summary>
    public sealed record TextDelta(string Text) : AgentEvent;

    /// <summary>The LLM is invoking a tool.</summary>
    public sealed record ToolUse(string ToolName, string CallId, IDictionary<string, object?>? Arguments) : AgentEvent;

    /// <summary>Incremental output from a running tool, for live display to the human.</summary>
    public sealed record ToolProgress(string ToolName, string CallId, string Text) : AgentEvent;

    /// <summary>
    /// A tool failed, and the failure was also returned to the model as a tool
    /// result so the current agent turn can choose a recovery action.
    /// </summary>
    public sealed record ToolFailure(
        string ToolName,
        string CallId,
        string Message,
        Exception? Exception = null) : AgentEvent;

    /// <summary>A tool produced its final result (the block fed back to the LLM).</summary>
    public sealed record ToolResult(string ToolName, string CallId, string Result) : AgentEvent;

    /// <summary>
    /// A tool call was refused by the permission engine before it ran (a deny rule,
    /// a declined confirmation, or a headless fail-closed). The <paramref name="Reason"/>
    /// is also fed back to the LLM as the tool result so the model can adapt.
    /// </summary>
    public sealed record ToolDenied(string ToolName, string CallId, string Reason) : AgentEvent;

    /// <summary>A context-compaction transaction committed before a model call.</summary>
    public sealed record CompactionCompleted(CompactionReport Report) : AgentEvent;

    /// <summary>A terminal agent error occurred and the current turn cannot continue.</summary>
    public sealed record Error(string Message, Exception? Exception = null) : AgentEvent;
}
