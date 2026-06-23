using Microsoft.Extensions.AI;

namespace Astra.Core.Permissions;

/// <summary>
/// The default policy ("Y"): a behavior-class default with per-command rule
/// exceptions. The bulk decision comes from the call's <see cref="ToolAction"/> —
/// <see cref="ToolAction.Read"/> is allowed without prompting; everything else
/// (Write / Execute / Other) requires confirmation — so a host can "allow all
/// reads" once instead of re-prompting on every argument change. Rules layer
/// exceptions on top: a Deny for a dangerous command, or a pre-Allow for a known
/// safe one.
/// </summary>
/// <remarks>
/// D5. This is the two-layer model Astra's CLAUDE.md specified at D2 (Classify for
/// the bulk class decision + a rule engine for per-command exceptions). Rule
/// precedence is most-restrictive-first (Deny &gt; Ask &gt; Allow): if any matching
/// rule denies, the result is Deny regardless of other matches. With no matching
/// rule, the class default applies — so this policy never returns
/// <see cref="PolicyVerdict.NoOpinion"/> (it always has an opinion). Synchronous in
/// practice: it touches only in-memory rules, so it returns a completed ValueTask.
/// </remarks>
public sealed class ClassDefaultPolicy(IReadOnlyList<PermissionRule>? rules = null) : IPermissionPolicy
{
    private readonly IReadOnlyList<PermissionRule> _rules = rules ?? [];

    public ValueTask<PolicyVerdict> EvaluateAsync(
        FunctionCallContent call, ToolAction action, CancellationToken ct)
    {
        // Rule exceptions first, most-restrictive-first: a single matching Deny wins
        // over any Allow/Ask; otherwise a matching Ask wins over an Allow. Scan once
        // and remember the strongest matched behavior.
        RuleBehavior? strongest = null;
        string? denyReason = null;
        string? askMessage = null;
        foreach (var rule in _rules)
        {
            if (!rule.Matches(call))
                continue;
            if (strongest is null || rule.Behavior > strongest)
                strongest = rule.Behavior;
            if (rule.Behavior == RuleBehavior.Deny)
                denyReason ??= $"Denied by permission rule for tool '{rule.ToolName}'.";
            else if (rule.Behavior == RuleBehavior.Ask)
                askMessage ??= $"Tool '{call.Name}' requires confirmation (matched an ask rule).";
        }

        PolicyVerdict verdict = strongest switch
        {
            RuleBehavior.Deny => new PolicyVerdict.Deny(denyReason!),
            RuleBehavior.Ask => new PolicyVerdict.Ask(askMessage!),
            RuleBehavior.Allow => new PolicyVerdict.Allow(),
            // No matching rule: fall back to the behavior-class default.
            null => ClassDefault(call, action),
            _ => ClassDefault(call, action),
        };
        return ValueTask.FromResult(verdict);
    }

    /// <summary>
    /// The class default with no rule in play: reads run freely, everything else
    /// asks. Other (the fail-closed class from D2) asks too — never silently allows.
    /// </summary>
    private static PolicyVerdict ClassDefault(FunctionCallContent call, ToolAction action) =>
        action == ToolAction.Read
            ? new PolicyVerdict.Allow()
            : new PolicyVerdict.Ask(
                $"Tool '{call.Name}' wants to perform a {action} action. Allow it?");
}

/// <summary>
/// The strict policy ("X"): every call requires confirmation, regardless of class
/// or rules. Useful as a maximally-cautious host policy, or as a baseline to
/// compare the class-default behavior against.
/// </summary>
/// <remarks>D5. Mirrors Claude Code's "no rule matched -> ask" with no class
/// fast-path. Returns a completed ValueTask (no I/O).</remarks>
public sealed class AlwaysAskPolicy : IPermissionPolicy
{
    public ValueTask<PolicyVerdict> EvaluateAsync(
        FunctionCallContent call, ToolAction action, CancellationToken ct) =>
        ValueTask.FromResult<PolicyVerdict>(
            new PolicyVerdict.Ask($"Tool '{call.Name}' ({action}) requires confirmation. Allow it?"));
}
