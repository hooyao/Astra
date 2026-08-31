using System.Text.Json;

namespace Astra.Core;

/// <summary>The permission-relevant category of one tool invocation.</summary>
public enum ToolAction
{
    /// <summary>No side effects: observes state without changing it.</summary>
    Read,

    /// <summary>Mutates state in a recoverable way.</summary>
    Write,

    /// <summary>Runs arbitrary or potentially irreversible effects.</summary>
    Execute,

    /// <summary>Unclassified and therefore handled by the strictest policy.</summary>
    Other,
}

/// <summary>One item streamed from an executing tool.</summary>
public abstract record ToolOutput
{
    private ToolOutput() { }

    /// <summary>Incremental output for the human; never fed to the LLM.</summary>
    public sealed record Progress(string Text) : ToolOutput;

    /// <summary>The complete result fed back to the LLM.</summary>
    public sealed record Result(string Text) : ToolOutput;
}

/// <summary>
/// Immutable metadata available before an executor exists. AgentLoop uses this
/// definition to advertise the tool, classify calls, and run permission checks.
/// </summary>
public sealed class ToolDefinition
{
    private readonly Func<IDictionary<string, object?>?, ToolAction> _classify;

    public ToolDefinition(
        string name,
        string description,
        JsonElement inputSchema,
        Func<IDictionary<string, object?>?, ToolAction>? classify = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (inputSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A tool input schema must be a JSON object.", nameof(inputSchema));

        Name = name;
        Description = description;
        InputSchema = inputSchema;
        _classify = classify ?? FailClosed;
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }

    /// <summary>
    /// Classifies one invocation without activating its executor. Missing
    /// classifiers fail closed to <see cref="ToolAction.Other"/>.
    /// </summary>
    public ToolAction Classify(IDictionary<string, object?>? arguments) =>
        _classify(arguments);

    private static ToolAction FailClosed(IDictionary<string, object?>? _) =>
        ToolAction.Other;
}

/// <summary>Creates durable, immutable JSON schemas for static tool metadata.</summary>
public static class ToolSchema
{
    public static JsonElement Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// Invocation-time executable behavior. Implementations contain no advertised
/// metadata and are created only after a call passes classification and permission.
/// </summary>
public interface IToolExecutor
{
    IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        CancellationToken ct);
}

/// <summary>Activates an executor for one admitted tool invocation.</summary>
public interface IToolExecutorFactory
{
    IToolExecutor Create(string toolName);
}

/// <summary>Small adapter for tests and non-DI hosts.</summary>
public sealed class DelegateToolExecutorFactory(
    Func<string, IToolExecutor> factory) : IToolExecutorFactory
{
    public IToolExecutor Create(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return factory(toolName) ??
            throw new InvalidOperationException($"Tool executor factory returned null for '{toolName}'.");
    }
}
