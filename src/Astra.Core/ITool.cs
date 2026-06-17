using System.Text.Json;

namespace Astra.Core;

/// <summary>
/// The permission-relevant category of a single tool invocation, computed from
/// its arguments. This is the seam the permission engine (a later layer) keys
/// its decisions on — a *behavior class*, not a command string.
///
/// Why a class and not a command string: Claude Code keys bash approvals on the
/// command's exact/prefix string, so a small argument change misses the saved
/// rule and re-prompts the user — which makes the approval worthless in practice.
/// Classifying by behavior ("this is a read") lets a host approve a whole class
/// once ("allow all reads") and not re-prompt as arguments drift. The cost is
/// coarseness (a deny-list, added later, snipes individual dangerous commands).
///
/// Ordered loosely most-safe to least-safe; do not rely on the numeric order for
/// permission logic — match on the named value.
/// </summary>
public enum ToolAction
{
    /// <summary>No side effects: observes state without changing it (ls, cat, grep).</summary>
    Read,

    /// <summary>Mutates state in a recoverable way (touch, mkdir, write a file).</summary>
    Write,

    /// <summary>Runs arbitrary/irreversible effects (rm -rf, dd, a spawned process).</summary>
    Execute,

    /// <summary>
    /// Unclassified. The fail-closed bucket: anything a tool does not positively
    /// recognize lands here, and the permission engine must treat it as the most
    /// restrictive category. This is the default <see cref="ITool.Classify"/>
    /// returns when a tool does not override it.
    /// </summary>
    Other,
}

/// <summary>
/// One item streamed out of a tool during execution. A tool has two distinct
/// consumers with opposite needs, and this type keeps them separate:
///
///   - the human at the terminal wants output as it happens (a long build, a
///     <c>tail -f</c>), streamed in <see cref="Progress"/> chunks;
///   - the LLM wants exactly one complete <c>tool_result</c> block, delivered
///     once as the final <see cref="Result"/>.
///
/// Crucially the final Result is NOT required to be the concatenation of the
/// Progress chunks. A tool may stream "downloading… 50%…" to the human but hand
/// the model a terse "installed 5 packages". The loop forwards every Progress to
/// the consumer and feeds only the Result back into the conversation.
///
/// Contract: a well-behaved tool yields zero or more <see cref="Progress"/>
/// items, then exactly one <see cref="Result"/> as its last item. The loop
/// treats the last Result it sees as authoritative; a tool that yields no Result
/// is treated as having produced an empty result.
/// </summary>
public abstract record ToolOutput
{
    private ToolOutput() { }

    /// <summary>Incremental output for the human; never fed to the LLM.</summary>
    public sealed record Progress(string Text) : ToolOutput;

    /// <summary>The single complete result fed back to the LLM as the tool_result.</summary>
    public sealed record Result(string Text) : ToolOutput;
}

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

    /// <summary>
    /// Execute the tool, streaming output as it is produced. Yield
    /// <see cref="ToolOutput.Progress"/> items for the human as work happens, and
    /// exactly one <see cref="ToolOutput.Result"/> as the last item — the complete
    /// result handed to the LLM. See <see cref="ToolOutput"/> for the contract.
    /// </summary>
    IAsyncEnumerable<ToolOutput> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct);

    /// <summary>
    /// Classify what this invocation does, so the permission layer can decide on
    /// it. The decision is input-dependent: the same tool may read for one
    /// argument set and execute for another (bash "ls" vs "rm -rf").
    ///
    /// Default interface method, fail-closed: a tool that does not override this
    /// is treated as <see cref="ToolAction.Other"/> — the strictest bucket — so
    /// forgetting to classify is safe (over-restrictive), never unsafe.
    ///
    /// NOTE (C# default interface method): this default body is only reachable
    /// through an <see cref="ITool"/> reference. Code holding a concrete tool
    /// type that does not itself declare Classify must cast to ITool to call it.
    /// The agent loop always holds ITool, so dispatch is unaffected.
    /// </summary>
    ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Other;
}
