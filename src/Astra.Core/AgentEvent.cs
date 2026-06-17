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

    /// <summary>A tool produced its final result (the block fed back to the LLM).</summary>
    public sealed record ToolResult(string ToolName, string CallId, string Result) : AgentEvent;

    /// <summary>An error occurred during LLM call or tool execution.</summary>
    public sealed record Error(string Message, Exception? Exception = null) : AgentEvent;
}
