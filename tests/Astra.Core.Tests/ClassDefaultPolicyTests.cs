using Astra.Core;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D5 — the default policy ("Y"): a behavior-class default (Read allowed, else ask)
/// with per-command rule exceptions, most-restrictive-first. Pure and synchronous;
/// these tests pin the decision table. See
/// agent/experiments/d05-permission-pipeline/source-reconciliation.md.
/// </summary>
public class ClassDefaultPolicyTests
{
    private static FunctionCallContent Call(string tool = "bash", string? command = null)
    {
        var args = new Dictionary<string, object?>();
        if (command is not null) args["command"] = command;
        return new FunctionCallContent($"id-{tool}", tool, args);
    }

    private static async Task<PolicyVerdict> Eval(
        IPermissionPolicy policy, FunctionCallContent call, ToolAction action) =>
        await policy.EvaluateAsync(call, action, CancellationToken.None);

    // ------------------------------------------------------------------
    // The class default with no rules: Read runs freely; Write/Execute/Other ask.
    // Other (the D2 fail-closed class) must ask, never silently allow.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ClassDefault_ReadAllows_OthersAsk()
    {
        var policy = new ClassDefaultPolicy();

        Assert.IsType<PolicyVerdict.Allow>(await Eval(policy, Call(command: "ls"), ToolAction.Read));
        Assert.IsType<PolicyVerdict.Ask>(await Eval(policy, Call(command: "touch f"), ToolAction.Write));
        Assert.IsType<PolicyVerdict.Ask>(await Eval(policy, Call(command: "rm -rf /"), ToolAction.Execute));
        Assert.IsType<PolicyVerdict.Ask>(await Eval(policy, Call(command: "curl x"), ToolAction.Other));
    }

    // ------------------------------------------------------------------
    // A deny rule overrides the class default: even a Read is denied if a rule says so.
    // ------------------------------------------------------------------
    [Fact]
    public async Task DenyRule_OverridesClassDefault_EvenForRead()
    {
        var policy = new ClassDefaultPolicy([
            new PermissionRule("bash", RuleBehavior.Deny, CommandPrefix: "ls /secret"),
        ]);

        // A normal read still allows...
        Assert.IsType<PolicyVerdict.Allow>(await Eval(policy, Call(command: "ls /tmp"), ToolAction.Read));
        // ...but the denied prefix is refused despite being a Read.
        Assert.IsType<PolicyVerdict.Deny>(await Eval(policy, Call(command: "ls /secret/keys"), ToolAction.Read));
    }

    // ------------------------------------------------------------------
    // An allow rule pre-approves a command that would otherwise ask (a Write).
    // ------------------------------------------------------------------
    [Fact]
    public async Task AllowRule_PreApproves_WhatWouldOtherwiseAsk()
    {
        var policy = new ClassDefaultPolicy([
            new PermissionRule("bash", RuleBehavior.Allow, CommandPrefix: "touch /tmp/safe"),
        ]);

        // Pre-allowed write runs without asking...
        Assert.IsType<PolicyVerdict.Allow>(await Eval(policy, Call(command: "touch /tmp/safe.txt"), ToolAction.Write));
        // ...an unlisted write still asks.
        Assert.IsType<PolicyVerdict.Ask>(await Eval(policy, Call(command: "touch /etc/passwd"), ToolAction.Write));
    }

    // ------------------------------------------------------------------
    // Precedence is most-restrictive-first: when both an allow and a deny rule match
    // the same call, Deny wins.
    // ------------------------------------------------------------------
    [Fact]
    public async Task RulePrecedence_DenyBeatsAllow_WhenBothMatch()
    {
        var policy = new ClassDefaultPolicy([
            new PermissionRule("bash", RuleBehavior.Allow, CommandPrefix: "git"),
            new PermissionRule("bash", RuleBehavior.Deny, CommandPrefix: "git push"),
        ]);

        Assert.IsType<PolicyVerdict.Allow>(await Eval(policy, Call(command: "git status"), ToolAction.Read));
        Assert.IsType<PolicyVerdict.Deny>(await Eval(policy, Call(command: "git push origin main"), ToolAction.Execute));
    }

    // ------------------------------------------------------------------
    // A whole-tool rule (no CommandPrefix) matches every invocation of the tool.
    // ------------------------------------------------------------------
    [Fact]
    public async Task WholeToolRule_MatchesEveryInvocation()
    {
        var policy = new ClassDefaultPolicy([
            new PermissionRule("dangerous_tool", RuleBehavior.Deny),
        ]);

        Assert.IsType<PolicyVerdict.Deny>(
            await Eval(policy, Call(tool: "dangerous_tool"), ToolAction.Read));
    }

    // ------------------------------------------------------------------
    // AlwaysAskPolicy ("X"): every call asks, even a Read.
    // ------------------------------------------------------------------
    [Fact]
    public async Task AlwaysAskPolicy_AsksEvenForRead()
    {
        var policy = new AlwaysAskPolicy();

        Assert.IsType<PolicyVerdict.Ask>(await Eval(policy, Call(command: "ls"), ToolAction.Read));
        Assert.IsType<PolicyVerdict.Ask>(await Eval(policy, Call(command: "rm -rf /"), ToolAction.Execute));
    }
}
