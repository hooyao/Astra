using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// A fake IChatClient that "acts" instead of calling a real LLM. It is stateless
/// about turn count — exactly like a real model, it decides what to emit purely
/// from the conversation it is handed each call (look at the last message). This
/// mirrors the real contract and avoids baking the loop's internal cadence into
/// the test.
///
/// Dispatch rule for D1:
///   last message Role == Tool   -> tool result is already in context, emit a
///                                  plain-text final answer (leads to end_turn).
///   otherwise (User)            -> emit a tool_use for get_current_time.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public int CallCount { get; private set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        await Task.CompletedTask; // async iterator, no real awaits — suppress CS1998

        var last = messages.Last();

        if (last.Role == ChatRole.Tool)
        {
            // Second round-trip: tool result is in the transcript, give the
            // natural-language finish. No tool call -> loop will end_turn.
            yield return new ChatResponseUpdate(ChatRole.Assistant, "It is 3pm.");
        }
        else
        {
            // First round-trip: ask to run the tool.
            var call = new FunctionCallContent("call-1", "get_current_time", null);
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { call });
        }
    }

    // AgentLoop only streams. Fail closed so a wrong call is loud.
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ScriptedChatClient only supports streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A trivial deterministic tool so the tool_use path has something to dispatch to.
/// Streams a single Result chunk (no Progress) — the minimal well-behaved tool.
/// </summary>
internal sealed class FakeTimeTool : IToolExecutor
{
    public const string ToolName = "get_current_time";

    public static ToolDefinition Definition { get; } = new(
        ToolName,
        "Get the current time.",
        ToolSchema.Parse("{\"type\":\"object\",\"properties\":{}}"),
        static _ => ToolAction.Read);

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield return new ToolOutput.Result("2026-06-15 15:00:00");
    }
}

/// <summary>
/// The simplest possible model: always replies with one text chunk, never a tool
/// call. Used to prove the loop terminates after a single round-trip on end_turn.
/// </summary>
internal sealed class TextOnlyChatClient : IChatClient
{
    public int CallCount { get; private set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        await Task.CompletedTask;
        yield return new ChatResponseUpdate(ChatRole.Assistant, "Hello, world.");
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TextOnlyChatClient only supports streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

public class AgentLoopTests
{
    // ------------------------------------------------------------------
    // TEST 1 — text-only response terminates (one round-trip, no tools).
    // If the loop did not honor end_turn it would never return and the test
    // would hang; the fact that ToListAsync completes is half the proof. The
    // other half: exactly one TextDelta, no tool events, one model call.
    // ------------------------------------------------------------------
    [Fact]
    public async Task TextOnlyResponse_TerminatesOnEndTurn()
    {
        var model = new TextOnlyChatClient();
        var loop = new AgentLoop(model, toolDefinitions: []);

        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("hi"))
            events.Add(evt);

        Assert.Equal(1, model.CallCount);                       // one round-trip
        var delta = Assert.Single(events.OfType<AgentEvent.TextDelta>());
        Assert.Equal("Hello, world.", delta.Text);
        Assert.Empty(events.OfType<AgentEvent.ToolUse>());      // no tools involved
        Assert.IsType<AgentEvent.TextDelta>(events[^1]);        // ended on text
    }

    // ------------------------------------------------------------------
    // TEST 2 — tool_use then end_turn: TWO round-trips, LAST event is TextDelta
    // (NOT ToolResult). This is the assertion that catches the "stopped too
    // early" bug: bad code's last event is ToolResult; correct code goes back
    // to the model once more and ends on the natural-language finish.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ToolUseThenText_RunsTwoRoundTrips_AndEndsOnText()
    {
        var model = new ScriptedChatClient();
        var loop = new AgentLoop(
            model,
            [FakeTimeTool.Definition],
            toolExecutorFactory: new DelegateToolExecutorFactory(_ => new FakeTimeTool()));

        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("what time is it?"))
            events.Add(evt);

        // Two round-trips: first emits the tool call, second emits the answer.
        Assert.Equal(2, model.CallCount);

        // Event order: ToolUse -> ToolResult -> TextDelta.
        Assert.Collection(events,
            e => Assert.IsType<AgentEvent.ToolUse>(e),
            e => Assert.IsType<AgentEvent.ToolResult>(e),
            e => Assert.IsType<AgentEvent.TextDelta>(e));

        // The load-bearing assertion: it did NOT stop on the tool result.
        Assert.IsType<AgentEvent.TextDelta>(events[^1]);

        // The tool actually ran and its CallId round-tripped.
        var use = Assert.IsType<AgentEvent.ToolUse>(events[0]);
        var result = Assert.IsType<AgentEvent.ToolResult>(events[1]);
        Assert.Equal("get_current_time", use.ToolName);
        Assert.Equal(use.CallId, result.CallId);
        Assert.Equal("2026-06-15 15:00:00", result.Result);
    }
}
