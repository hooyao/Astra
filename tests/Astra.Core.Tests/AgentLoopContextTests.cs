using System.Runtime.CompilerServices;
using Astra.Core;
using Astra.Core.Context;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D6 — context assembly. These tests assert the wire-level invariants we verified
/// against a real Claude Code request trace: the a+b system prefix is byte-stable
/// across turns (so a prompt cache keeps hitting it), layer-c attachments vary per
/// turn and ride on the user message, and a hung attachment provider is dropped at
/// the deadline instead of stalling the turn.
/// See agent/experiments/d06-context-assembly/.
/// </summary>
public class AgentLoopContextTests
{
    /// <summary>
    /// A text-only fake that snapshots the FULL message list it is handed on every
    /// call. That captured history is what lets a test hash the system prefix across
    /// turns — the same thing we did by hashing system[] across seq 3/5/6 in the
    /// real trace.
    /// </summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public List<List<ChatMessage>> Calls { get; } = [];

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList()); // snapshot this turn's context
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok"); // no tool -> end_turn
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static string SystemText(List<ChatMessage> turn) =>
        turn.First(m => m.Role == ChatRole.System).Text;

    private static string LastUserText(List<ChatMessage> turn) =>
        turn.Last(m => m.Role == ChatRole.User).Text;

    // A layer-b provider whose value would CHANGE if it were re-read each turn — it
    // returns a different string every call. Wrapped in MemoizedSessionContext, only
    // the first call's value should ever appear. This is the "git status changes
    // mid-session but the prefix stays frozen" scenario, made deterministic.
    private sealed class CountingSessionContext : ISessionContextProvider
    {
        private int _n;
        public int Calls => _n;
        public ValueTask<string> GetAsync(CancellationToken ct = default) =>
            new($"session-context-v{Interlocked.Increment(ref _n)}");
    }

    // ------------------------------------------------------------------
    // TEST 1 — the a+b system prefix is byte-identical across three turns, even
    // though b's underlying provider would return a new value each time. This is
    // the core cache-stability invariant, and it also proves memoization: the
    // provider is invoked exactly once.
    // ------------------------------------------------------------------
    [Fact]
    public async Task SystemPrefix_IsByteStable_AcrossTurns_AndBIsMemoized()
    {
        var model = new CapturingChatClient();
        var b = new CountingSessionContext();
        var loop = new AgentLoop(
            model, toolDefinitions: [], systemPrompt: "You are Astra.",
            sessionContext: new MemoizedSessionContext(b));

        for (var i = 0; i < 3; i++)
            await foreach (var _ in loop.SubmitAsync($"turn {i}")) { }

        Assert.Equal(3, model.Calls.Count);
        var s0 = SystemText(model.Calls[0]);
        var s1 = SystemText(model.Calls[1]);
        var s2 = SystemText(model.Calls[2]);

        // Byte-stable across turns (what the prompt cache needs).
        Assert.Equal(s0, s1);
        Assert.Equal(s1, s2);

        // a and b are both present, a first.
        Assert.StartsWith("You are Astra.", s0);
        Assert.Contains("session-context-v1", s0);

        // Memoized: b ran once, and it is the FIRST value that stuck (not v2/v3).
        Assert.Equal(1, b.Calls);
        Assert.DoesNotContain("session-context-v2", s2);
    }

    // ------------------------------------------------------------------
    // TEST 2 — layer c varies per turn and rides on the user message, while the
    // system prefix is unaffected. A PeriodicReminderProvider(every=2) emits only
    // on turn 2 and 4, so turns 1/3 have a bare user message and turns 2/4 carry
    // the attachment — exactly the msg[8]==[13]==[20] periodic-injection pattern.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Attachments_VaryPerTurn_OnUserMessage_PrefixUnaffected()
    {
        var model = new CapturingChatClient();
        var reminder = new PeriodicReminderProvider("REMINDER-TEXT", everyNTurns: 2);
        var loop = new AgentLoop(
            model, toolDefinitions: [], systemPrompt: "You are Astra.",
            attachmentProviders: [reminder]);

        for (var i = 1; i <= 4; i++)
            await foreach (var _ in loop.SubmitAsync($"turn {i}")) { }

        Assert.Equal(4, model.Calls.Count);

        // Turns 1 and 3: no attachment injected.
        Assert.DoesNotContain("REMINDER-TEXT", LastUserText(model.Calls[0]));
        Assert.DoesNotContain("REMINDER-TEXT", LastUserText(model.Calls[2]));

        // Turns 2 and 4: attachment present, and it is on the USER message, wrapped.
        Assert.Contains("REMINDER-TEXT", LastUserText(model.Calls[1]));
        Assert.Contains("<attachment name=\"reminder\">", LastUserText(model.Calls[1]));
        Assert.Contains("REMINDER-TEXT", LastUserText(model.Calls[3]));

        // The system prefix never absorbed the attachment and stayed stable.
        Assert.Equal(SystemText(model.Calls[0]), SystemText(model.Calls[3]));
        Assert.DoesNotContain("REMINDER-TEXT", SystemText(model.Calls[1]));
    }

    // A layer-c provider that hangs far past the deadline. It respects the token, so
    // when the gatherer cancels at the deadline it unblocks and is dropped.
    private sealed class HangingProvider : IAttachmentProvider
    {
        public string Name => "hangs";
        public async ValueTask<string?> GetAsync(CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct); // way past the deadline
            return "SHOULD-NOT-APPEAR";
        }
    }

    // A fast, well-behaved provider that returns immediately.
    private sealed class FastProvider : IAttachmentProvider
    {
        public string Name => "fast";
        public ValueTask<string?> GetAsync(CancellationToken ct = default) =>
            new("FAST-OK");
    }

    // ------------------------------------------------------------------
    // TEST 3 — a hung provider is dropped at the deadline; the turn still sends,
    // the fast provider's content survives, and the whole gather finishes near the
    // deadline (not after 30s). This is the getAttachments() 1s-timeout guarantee:
    // one slow source cannot hold the turn hostage.
    // ------------------------------------------------------------------
    [Fact]
    public async Task HungAttachmentProvider_DroppedAtDeadline_TurnStillSends()
    {
        var model = new CapturingChatClient();
        var loop = new AgentLoop(
            model, toolDefinitions: [], systemPrompt: "You are Astra.",
            attachmentProviders: [new FastProvider(), new HangingProvider()],
            attachmentDeadline: TimeSpan.FromMilliseconds(200));

        var start = Environment.TickCount64;
        await foreach (var _ in loop.SubmitAsync("go")) { }
        var elapsed = Environment.TickCount64 - start;

        // The turn completed (the model was called).
        Assert.Single(model.Calls);
        var user = LastUserText(model.Calls[0]);

        // Fast provider survived; hung provider was dropped.
        Assert.Contains("FAST-OK", user);
        Assert.DoesNotContain("SHOULD-NOT-APPEAR", user);

        // Bounded by the deadline, nowhere near the 30s hang (generous CI margin).
        Assert.True(elapsed < 5000, $"gather took {elapsed}ms; the hung provider was not bounded by the deadline");
    }

    // ------------------------------------------------------------------
    // TEST 4 — no providers supplied reproduces pre-D6 behavior exactly: a bare
    // system prompt, a bare user message, nothing injected. Guards the
    // backward-compat contract the constructor promises.
    // ------------------------------------------------------------------
    [Fact]
    public async Task NoProviders_ReproducesPreD6Behavior()
    {
        var model = new CapturingChatClient();
        var loop = new AgentLoop(model, toolDefinitions: [], systemPrompt: "You are Astra.");

        await foreach (var _ in loop.SubmitAsync("hello")) { }

        var turn = model.Calls[0];
        Assert.Equal("You are Astra.", SystemText(turn));   // a only, no b appended
        Assert.Equal("hello", LastUserText(turn));           // user message untouched
    }
}
