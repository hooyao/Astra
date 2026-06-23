using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D5 — the engine orchestration: validate -> policy -> (if Ask) confirm, always
/// resolving to a terminal Allow/Deny. Confirmation is the only async step; the
/// headless path (no confirmer) is fail-closed to Deny.
/// </summary>
public class DefaultPermissionEngineTests
{
    // A do-nothing tool, just to populate the tool map for Layer-1 existence checks.
    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ToolOutput.Result("ok");
        }
    }

    // A confirmer with a fixed answer, recording whether it was consulted.
    private sealed class FakeConfirmation(bool answer) : IUserConfirmation
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(FunctionCallContent call, string message, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    private static IReadOnlyDictionary<string, ITool> Tools(params string[] names) =>
        names.ToDictionary(n => n, n => (ITool)new StubTool(n));

    private static FunctionCallContent Call(string tool, string? command = null)
    {
        var args = new Dictionary<string, object?>();
        if (command is not null) args["command"] = command;
        return new FunctionCallContent($"id-{tool}", tool, args);
    }

    // ------------------------------------------------------------------
    // Layer 1: an unknown tool is denied before the policy is consulted.
    // ------------------------------------------------------------------
    [Fact]
    public async Task UnknownTool_DeniedAtLayer1()
    {
        var engine = new DefaultPermissionEngine(Tools("bash"), new AlwaysAskPolicy());

        var decision = await engine.CheckAsync(Call("ghost"), ToolAction.Read, CancellationToken.None);

        var deny = Assert.IsType<PermissionDecision.Deny>(decision);
        Assert.Contains("Unknown tool", deny.Reason);
    }

    // ------------------------------------------------------------------
    // A policy Allow passes straight through to a terminal Allow; the confirmer is
    // never consulted (a Read under the class default does not ask).
    // ------------------------------------------------------------------
    [Fact]
    public async Task PolicyAllow_ResolvesAllow_WithoutConfirming()
    {
        var confirmer = new FakeConfirmation(answer: false); // would deny if consulted
        var engine = new DefaultPermissionEngine(Tools("bash"), new ClassDefaultPolicy(), confirmer);

        var decision = await engine.CheckAsync(Call("bash", "ls"), ToolAction.Read, CancellationToken.None);

        Assert.IsType<PermissionDecision.Allow>(decision);
        Assert.Equal(0, confirmer.Calls); // a Read never asked
    }

    // ------------------------------------------------------------------
    // An Ask with an approving confirmer becomes Allow; with a declining one, Deny.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Ask_Approved_BecomesAllow()
    {
        var confirmer = new FakeConfirmation(answer: true);
        var engine = new DefaultPermissionEngine(Tools("bash"), new ClassDefaultPolicy(), confirmer);

        var decision = await engine.CheckAsync(Call("bash", "rm -rf x"), ToolAction.Execute, CancellationToken.None);

        Assert.IsType<PermissionDecision.Allow>(decision);
        Assert.Equal(1, confirmer.Calls);
    }

    [Fact]
    public async Task Ask_Declined_BecomesDeny()
    {
        var confirmer = new FakeConfirmation(answer: false);
        var engine = new DefaultPermissionEngine(Tools("bash"), new ClassDefaultPolicy(), confirmer);

        var decision = await engine.CheckAsync(Call("bash", "rm -rf x"), ToolAction.Execute, CancellationToken.None);

        var deny = Assert.IsType<PermissionDecision.Deny>(decision);
        Assert.Contains("declined", deny.Reason);
        Assert.Equal(1, confirmer.Calls);
    }

    // ------------------------------------------------------------------
    // Headless fail-closed: an Ask with NO confirmer configured resolves to Deny,
    // never a silent Allow.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Ask_Headless_FailsClosedToDeny()
    {
        var engine = new DefaultPermissionEngine(Tools("bash"), new ClassDefaultPolicy(), confirmation: null);

        var decision = await engine.CheckAsync(Call("bash", "rm -rf x"), ToolAction.Execute, CancellationToken.None);

        var deny = Assert.IsType<PermissionDecision.Deny>(decision);
        Assert.Contains("headless", deny.Reason);
    }

    // ------------------------------------------------------------------
    // A policy Deny short-circuits: terminal Deny, confirmer never consulted.
    // ------------------------------------------------------------------
    [Fact]
    public async Task PolicyDeny_ResolvesDeny_WithoutConfirming()
    {
        var confirmer = new FakeConfirmation(answer: true); // would allow if consulted
        var policy = new ClassDefaultPolicy([new PermissionRule("bash", RuleBehavior.Deny, "rm")]);
        var engine = new DefaultPermissionEngine(Tools("bash"), policy, confirmer);

        var decision = await engine.CheckAsync(Call("bash", "rm -rf x"), ToolAction.Execute, CancellationToken.None);

        Assert.IsType<PermissionDecision.Deny>(decision);
        Assert.Equal(0, confirmer.Calls); // deny rule short-circuited before asking
    }
}
