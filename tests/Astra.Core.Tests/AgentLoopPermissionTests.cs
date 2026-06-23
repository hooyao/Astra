using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D5 — the permission gate wired into the loop. The load-bearing guarantee: when
/// the engine denies a call, the tool's ExecuteAsync is NEVER entered (the side
/// effect does not happen), and the deny reason is fed back to the LLM as the tool
/// result. When allowed, the tool runs normally.
/// </summary>
public class AgentLoopPermissionTests
{
    // A tool that records whether it was actually executed — the by-construction
    // proof that a denied call never runs.
    private sealed class SpyTool(string name, ToolAction action) : ITool
    {
        public int Executions { get; private set; }
        public string Name => name;
        public string Description => "spy";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();

        public ToolAction Classify(IDictionary<string, object?>? arguments) => action;

        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Executions++;
            await Task.CompletedTask;
            yield return new ToolOutput.Result($"{name}-ran");
        }
    }

    // Scripted client: emit one tool call on the first turn, a final text on the next.
    private sealed class OneToolClient(FunctionCallContent call) : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            if (messages.Last().Role == ChatRole.Tool)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
                yield break;
            }
            yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { call });
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("streaming only");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedConfirmation(bool answer) : IUserConfirmation
    {
        public Task<bool> ConfirmAsync(FunctionCallContent call, string message, CancellationToken ct) =>
            Task.FromResult(answer);
    }

    private static FunctionCallContent Cmd(string tool, string command) =>
        new($"id-{tool}", tool, new Dictionary<string, object?> { ["command"] = command });

    // ------------------------------------------------------------------
    // [the load-bearing D5 test] A denied call never executes the tool, and the
    // deny reason is returned to the LLM as the tool result.
    // ------------------------------------------------------------------
    [Fact]
    public async Task DeniedCall_ToolNeverRuns_ReasonFedToLlm()
    {
        var spy = new SpyTool("bash", ToolAction.Execute);
        var policy = new ClassDefaultPolicy([new PermissionRule("bash", RuleBehavior.Deny, "rm")]);
        var engine = new DefaultPermissionEngine(
            new Dictionary<string, ITool> { ["bash"] = spy }, policy);
        var loop = new AgentLoop(new OneToolClient(Cmd("bash", "rm -rf /")), [spy], permissionEngine: engine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        Assert.Equal(0, spy.Executions); // the tool body never ran
        Assert.Contains(events, e => e is AgentEvent.ToolDenied);
        // The deny reason became the tool_result fed back (not a "bash-ran" output).
        var result = events.OfType<AgentEvent.ToolResult>().Single();
        Assert.Contains("Denied", result.Result);
        Assert.DoesNotContain("ran", result.Result);
    }

    // ------------------------------------------------------------------
    // An allowed call runs the tool normally (Read under the class default).
    // ------------------------------------------------------------------
    [Fact]
    public async Task AllowedCall_ToolRuns()
    {
        var spy = new SpyTool("bash", ToolAction.Read);
        var engine = new DefaultPermissionEngine(
            new Dictionary<string, ITool> { ["bash"] = spy }, new ClassDefaultPolicy());
        var loop = new AgentLoop(new OneToolClient(Cmd("bash", "ls")), [spy], permissionEngine: engine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        Assert.Equal(1, spy.Executions);
        Assert.DoesNotContain(events, e => e is AgentEvent.ToolDenied);
        Assert.Contains("bash-ran", events.OfType<AgentEvent.ToolResult>().Single().Result);
    }

    // ------------------------------------------------------------------
    // An Ask resolved by a declining confirmer also blocks execution end-to-end.
    // ------------------------------------------------------------------
    [Fact]
    public async Task AskDeclined_ToolNeverRuns()
    {
        var spy = new SpyTool("bash", ToolAction.Execute);
        var engine = new DefaultPermissionEngine(
            new Dictionary<string, ITool> { ["bash"] = spy },
            new ClassDefaultPolicy(),
            new FixedConfirmation(answer: false));
        var loop = new AgentLoop(new OneToolClient(Cmd("bash", "rm -rf /")), [spy], permissionEngine: engine);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        Assert.Equal(0, spy.Executions);
        Assert.Contains(events, e => e is AgentEvent.ToolDenied);
    }

    // ------------------------------------------------------------------
    // Backward compatibility: with NO engine configured the loop is unguarded — the
    // tool runs as it did pre-D5.
    // ------------------------------------------------------------------
    [Fact]
    public async Task NoEngine_Unguarded_ToolRuns()
    {
        var spy = new SpyTool("bash", ToolAction.Execute);
        var loop = new AgentLoop(new OneToolClient(Cmd("bash", "rm -rf /")), [spy]); // no engine

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in loop.SubmitAsync("go", cts.Token)) { }

        Assert.Equal(1, spy.Executions); // unguarded: ran despite being an Execute
    }
}
