using System.Text.Json;

namespace Astra.Core;

/// <summary>
/// A tool that the agent can invoke during its execution loop.
/// </summary>
public interface ITool
{
    /// <summary>Tool name used by the LLM to invoke this tool.</summary>
    string Name { get; }

    /// <summary>Description shown to the LLM for tool selection.</summary>
    string Description { get; }

    /// <summary>JSON Schema describing the tool's input parameters.</summary>
    JsonElement InputSchema { get; }

    /// <summary>Execute the tool with the given arguments.</summary>
    ValueTask<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct);
}
