using Microsoft.Extensions.AI;

namespace Astra.Core.Permissions;

/// <summary>
/// A policy's verdict for a single tool call. Distinct from
/// <see cref="PermissionDecision"/> by one extra case — <see cref="NoOpinion"/>:
/// "this policy has no rule for this call; fall back to the engine's default." The
/// engine maps NoOpinion to its class-based fallback; a policy never leaves the
/// FINAL decision as NoOpinion, and the engine never propagates NoOpinion to its
/// caller.
/// </summary>
/// <remarks>
/// D5. This is Claude Code's `passthrough`, but confined to the policy boundary so
/// it does not leak into the engine as a third internal behavior (one of the CC
/// complexities the reconciliation note flags to avoid).
/// </remarks>
public abstract record PolicyVerdict
{
    private PolicyVerdict() { }

    /// <summary>Allow the call, optionally rewriting its arguments.</summary>
    public sealed record Allow(IDictionary<string, object?>? UpdatedArguments = null) : PolicyVerdict;

    /// <summary>Refuse the call; <paramref name="Reason"/> is surfaced to the LLM.</summary>
    public sealed record Deny(string Reason) : PolicyVerdict;

    /// <summary>Require human confirmation; <paramref name="Message"/> is shown.</summary>
    public sealed record Ask(string Message) : PolicyVerdict;

    /// <summary>
    /// No rule in this policy applies to the call; defer to the engine's class-based
    /// fallback (Read -> Allow, Write/Execute/Other -> Ask). Singleton to avoid
    /// per-call allocation on the common "no exception rule" path.
    /// </summary>
    public sealed record NoOpinion : PolicyVerdict
    {
        public static readonly NoOpinion Instance = new();
    }
}
