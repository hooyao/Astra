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

    /// <summary>A tool produced a result.</summary>
    public sealed record ToolResult(string ToolName, string CallId, string Result) : AgentEvent;

    /// <summary>An error occurred during LLM call or tool execution.</summary>
    public sealed record Error(string Message, Exception? Exception = null) : AgentEvent;
}
