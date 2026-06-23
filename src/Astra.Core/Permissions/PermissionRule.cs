using Microsoft.Extensions.AI;

namespace Astra.Core.Permissions;

/// <summary>
/// The behavior a matched <see cref="PermissionRule"/> imposes. Ordered for
/// precedence: when several rules match one call, the most restrictive wins
/// (Deny &gt; Ask &gt; Allow), mirroring Claude Code's deny-before-allow evaluation.
/// </summary>
public enum RuleBehavior
{
    /// <summary>Allow the call without prompting.</summary>
    Allow,

    /// <summary>Require human confirmation.</summary>
    Ask,

    /// <summary>Refuse the call outright.</summary>
    Deny,
}

/// <summary>
/// One permission exception rule: "for tool <see cref="ToolName"/> (optionally only
/// when its command starts with <see cref="CommandPrefix"/>), apply
/// <see cref="Behavior"/>." Rules are the per-command exception layer ON TOP of the
/// behavior-class default — e.g. bash is Execute-by-default-Ask, but a rule can
/// Deny <c>Bash("rm -rf")</c> or pre-Allow <c>Bash("ls")</c>.
/// </summary>
/// <remarks>
/// D5. A deliberately minimal slice of Claude Code's PermissionRule
/// (src/types/permissions.ts): CC carries a source tier (policy/user/project/
/// session) and supports prefix/wildcard/exact command matching. Here we keep
/// toolName + an optional command prefix; the source-tier ordering and wildcard
/// matching are noted for a later expansion. A rule with no
/// <see cref="CommandPrefix"/> matches every invocation of the tool.
/// </remarks>
public sealed record PermissionRule(
    string ToolName,
    RuleBehavior Behavior,
    string? CommandPrefix = null)
{
    /// <summary>
    /// Does this rule apply to <paramref name="call"/>? Matches on tool name, then —
    /// if a <see cref="CommandPrefix"/> is set — on whether the call's command
    /// argument starts with that prefix. The command is read from the conventional
    /// "command" argument (the bash tool's input); a rule with a CommandPrefix never
    /// matches a tool that has no such argument.
    /// </summary>
    public bool Matches(FunctionCallContent call)
    {
        if (!string.Equals(call.Name, ToolName, StringComparison.Ordinal))
            return false;

        if (CommandPrefix is null)
            return true; // whole-tool rule

        var command = GetCommand(call.Arguments);
        return command is not null
            && command.StartsWith(CommandPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Read the conventional "command" string from a call's argument bag, which may
    /// hold a plain string (tests) or a JsonElement (the wire). Returns null when
    /// absent — the same shape BashTool uses, kept local so rule matching does not
    /// depend on the tool.
    /// </summary>
    private static string? GetCommand(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("command", out var raw) || raw is null)
            return null;
        return raw switch
        {
            string s => s,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } je => je.GetString(),
            _ => raw.ToString(),
        };
    }
}
