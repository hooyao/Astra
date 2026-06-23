using Microsoft.Extensions.AI;

namespace Astra.Core.Permissions;

/// <summary>
/// WHAT the permission decision is, for a single classified call. The primary SDK
/// extension point: a host swaps this to change policy without touching the
/// confirmation UI or the engine's orchestration.
/// </summary>
/// <remarks>
/// D5. Default implementations:
/// <list type="bullet">
///   <item>ClassDefaultPolicy ("Y") — Read -> Allow, Write/Execute/Other -> Ask,
///   with an optional rule list for per-command allow/deny/ask EXCEPTIONS. This is
///   the two-layer model Astra's CLAUDE.md promised at D2: behavior-class bulk
///   decision + rule exceptions.</item>
///   <item>AlwaysAskPolicy ("X") — every call -> Ask.</item>
///   <item>(host-supplied) — load rules from a policy server, enforce CI deny-all,
///   etc.</item>
/// </list>
/// Async (<see cref="System.Threading.Tasks.ValueTask{TResult}"/>) so a host can do
/// I/O in a custom policy without a sync-over-async deadlock; ValueTask because the
/// default policy completes synchronously on the hot path (every tool call passes
/// through here and the common verdict needs no I/O), so it should not allocate a
/// Task. Per ValueTask rules the engine awaits the result exactly once and never
/// stores the ValueTask.
/// </remarks>
public interface IPermissionPolicy
{
    /// <param name="call">The tool call (Name, CallId, Arguments).</param>
    /// <param name="action">The call's behavior class from D2 Classify — the bulk
    /// signal the default policy keys on.</param>
    /// <param name="ct">Cancellation for any I/O a custom policy performs.</param>
    ValueTask<PolicyVerdict> EvaluateAsync(
        FunctionCallContent call, ToolAction action, CancellationToken ct);
}

/// <summary>
/// HOW an <see cref="PermissionDecision.Ask"/> is resolved — the only async,
/// possibly-suspending step in the pipeline. The engine owns no UI; it calls this.
/// </summary>
/// <remarks>
/// D5. Implementations: a CLI prompt ([y/N] at the terminal), a headless/service
/// auto-deny (log + refuse when no human is present), or a scripted test fake.
/// Semantically BLOCKING: confirming an Ask must gate the guarded tool's execution
/// — approval has to happen before the side effect, never after. (This is distinct
/// from a mid-turn user interrupt, which must NOT block; that is the deferred
/// control-plane work, not permission.) "Blocking" here is the await-once semantics,
/// not thread-blocking — the call frees its thread while waiting for the human.
/// </remarks>
public interface IUserConfirmation
{
    /// <returns>true = approved (the engine turns the Ask into Allow); false =
    /// declined (the engine turns it into Deny).</returns>
    Task<bool> ConfirmAsync(FunctionCallContent call, string message, CancellationToken ct);
}

/// <summary>
/// The orchestrator: input-validate -> policy -> (if Ask) confirm -> terminal
/// decision. The default (<see cref="DefaultPermissionEngine"/>) is what almost
/// everyone uses, customizing only the policy and confirmation above. Replacing the
/// whole engine is the escape hatch for a host that wants entirely different control
/// flow.
/// </summary>
public interface IPermissionEngine
{
    /// <summary>
    /// Decide whether <paramref name="call"/> may run. Always resolves to a terminal
    /// <see cref="PermissionDecision.Allow"/> or <see cref="PermissionDecision.Deny"/>
    /// — an Ask is resolved internally via <see cref="IUserConfirmation"/> before
    /// returning. Fail-closed: a validation failure, or an Ask with no interactive
    /// confirmer configured, resolves to Deny, never a silent Allow.
    /// </summary>
    Task<PermissionDecision> CheckAsync(
        FunctionCallContent call, ToolAction action, CancellationToken ct);
}
