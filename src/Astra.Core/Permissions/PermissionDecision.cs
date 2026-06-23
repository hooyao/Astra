using Microsoft.Extensions.AI;

namespace Astra.Core.Permissions;

/// <summary>
/// The outcome of evaluating one tool invocation against permission policy. A
/// three-state union, never a bool: <see cref="Ask"/> is a first-class result —
/// it is what "I cannot decide alone, a human must confirm" looks like, which a
/// bool cannot express.
/// </summary>
/// <remarks>
/// D5. Mirrors Claude Code's PermissionDecision (allow / deny / ask) in
/// src/types/permissions.ts, minus its internal `passthrough` state (we keep that
/// only at the policy boundary as <see cref="PolicyVerdict.NoOpinion"/>, never as
/// an engine-internal third behavior). See
/// agent/experiments/d05-permission-pipeline/source-reconciliation.md.
/// </remarks>
public abstract record PermissionDecision
{
    private PermissionDecision() { }

    /// <summary>
    /// Run the tool. <paramref name="UpdatedArguments"/> optionally replaces the
    /// call's arguments before execution (e.g. a policy that normalizes a path);
    /// null means run with the original arguments.
    /// </summary>
    public sealed record Allow(IDictionary<string, object?>? UpdatedArguments = null) : PermissionDecision;

    /// <summary>
    /// Do not run the tool. <paramref name="Reason"/> is fed back to the LLM as the
    /// tool result, so the model sees why the call was refused rather than the turn
    /// silently vanishing.
    /// </summary>
    public sealed record Deny(string Reason) : PermissionDecision;

    /// <summary>
    /// The engine cannot decide alone; a human must confirm. <paramref name="Message"/>
    /// is shown to the user. This is resolved through <see cref="IUserConfirmation"/>,
    /// not computed by the policy — the policy owns no UI. The engine never returns
    /// Ask to its caller: it resolves the Ask into a terminal Allow/Deny first.
    /// </summary>
    public sealed record Ask(string Message) : PermissionDecision;
}
