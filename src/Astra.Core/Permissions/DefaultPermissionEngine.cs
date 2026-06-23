using Microsoft.Extensions.AI;

namespace Astra.Core.Permissions;

/// <summary>
/// The default <see cref="IPermissionEngine"/>: orchestration only — the injectable
/// policy and confirmation do the real work. Layer order (a subset of Claude Code's
/// 7-layer pipeline; D5 builds layers 1, 2, 5):
/// <list type="number">
///   <item><b>Layer 1 — input validation.</b> Reject a call for an unknown tool
///   before any policy sees it.</item>
///   <item><b>Layer 2 — policy.</b> Ask the <see cref="IPermissionPolicy"/>. Its
///   verdict is Allow / Deny / Ask, or NoOpinion -> the engine falls back to Ask
///   (fail-closed: never a silent Allow).</item>
///   <item><b>Layer 5 — user confirmation.</b> If the verdict is Ask, call the
///   <see cref="IUserConfirmation"/>. Approved -> Allow, declined -> Deny. With no
///   confirmer configured (headless), Ask -> Deny.</item>
/// </list>
/// Always resolves to a terminal Allow/Deny — an Ask never escapes to the caller.
/// </summary>
/// <remarks>
/// D5. Layer 1 here is minimal (tool existence). Full JSON-Schema validation of
/// arguments against <see cref="ITool.InputSchema"/> is a later addition; the seam
/// is here so it slots in without changing the engine's shape.
/// TODO (permission layer): validate arguments against the tool's InputSchema.
/// Layers 3 (domain security beyond Classify), 4 (AI classifier), 6 (sandbox), and
/// 7 (workspace trust) are cited in the reconciliation note and deferred.
/// </remarks>
public sealed class DefaultPermissionEngine(
    IReadOnlyDictionary<string, ITool> tools,
    IPermissionPolicy policy,
    IUserConfirmation? confirmation = null) : IPermissionEngine
{
    public async Task<PermissionDecision> CheckAsync(
        FunctionCallContent call, ToolAction action, CancellationToken ct)
    {
        // Layer 1 — input validation. An unknown tool is refused here; the loop
        // also guards this, but permission must not assume a later layer will.
        if (!tools.ContainsKey(call.Name))
            return new PermissionDecision.Deny($"Unknown tool '{call.Name}'.");

        // Layer 2 — policy.
        var verdict = await policy.EvaluateAsync(call, action, ct);
        switch (verdict)
        {
            case PolicyVerdict.Allow allow:
                return new PermissionDecision.Allow(allow.UpdatedArguments);
            case PolicyVerdict.Deny deny:
                return new PermissionDecision.Deny(deny.Reason);
            case PolicyVerdict.Ask ask:
                return await ResolveAskAsync(call, ask.Message, ct);
            // NoOpinion: the policy declined to decide. Fail closed to Ask rather
            // than allowing — the engine has no rule either, so a human decides.
            case PolicyVerdict.NoOpinion:
                return await ResolveAskAsync(
                    call, $"Tool '{call.Name}' ({action}) has no policy decision. Allow it?", ct);
            default:
                return new PermissionDecision.Deny("Unrecognized policy verdict (fail-closed).");
        }
    }

    /// <summary>
    /// Layer 5. Turn an Ask into a terminal decision via the injected confirmer.
    /// Headless (no confirmer) is fail-closed: there is no human to approve, so Deny.
    /// </summary>
    private async Task<PermissionDecision> ResolveAskAsync(
        FunctionCallContent call, string message, CancellationToken ct)
    {
        if (confirmation is null)
            return new PermissionDecision.Deny(
                $"Tool '{call.Name}' requires confirmation but no confirmer is configured (headless: denied).");

        var approved = await confirmation.ConfirmAsync(call, message, ct);
        return approved
            ? new PermissionDecision.Allow()
            : new PermissionDecision.Deny($"Tool '{call.Name}' was declined by the user.");
    }
}
