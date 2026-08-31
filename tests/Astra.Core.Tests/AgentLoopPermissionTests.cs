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
    private sealed class SpyTool(string name) : IToolExecutor
    {
        public int Executions { get; private set; }

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

    private static ToolDefinition Definition(string name, ToolAction action) =>
        new(
            name,
            "spy",
            ToolSchema.Parse("{\"type\":\"object\"}"),
            _ => action);

    // ------------------------------------------------------------------
    // [the load-bearing D5 test] A denied call never executes the tool, and the
    // deny reason is returned to the LLM as the tool result.
    // ------------------------------------------------------------------
    [Fact]
    public async Task DeniedCall_ToolNeverRuns_ReasonFedToLlm()
    {
        var definition = Definition("bash", ToolAction.Execute);
        var executors = new CountingExecutorFactory(() => new SpyTool("bash"));
        var policy = new ClassDefaultPolicy([new PermissionRule("bash", RuleBehavior.Deny, "rm")]);
        var engine = new DefaultPermissionEngine([definition], policy);
        var loop = new AgentLoop(
            new OneToolClient(Cmd("bash", "rm -rf /")),
            [definition],
            permissionEngine: engine,
            toolExecutorFactory: executors);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        Assert.Equal(0, executors.Activations); // executor was never constructed
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
        var definition = Definition("bash", ToolAction.Read);
        var executors = new CountingExecutorFactory(() => new SpyTool("bash"));
        var engine = new DefaultPermissionEngine([definition], new ClassDefaultPolicy());
        var loop = new AgentLoop(
            new OneToolClient(Cmd("bash", "ls")),
            [definition],
            permissionEngine: engine,
            toolExecutorFactory: executors);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        Assert.Equal(1, executors.Activations);
        Assert.Equal(1, Assert.Single(executors.Instances).Executions);
        Assert.DoesNotContain(events, e => e is AgentEvent.ToolDenied);
        Assert.Contains("bash-ran", events.OfType<AgentEvent.ToolResult>().Single().Result);
    }

    // ------------------------------------------------------------------
    // An Ask resolved by a declining confirmer also blocks execution end-to-end.
    // ------------------------------------------------------------------
    [Fact]
    public async Task AskDeclined_ToolNeverRuns()
    {
        var definition = Definition("bash", ToolAction.Execute);
        var executors = new CountingExecutorFactory(() => new SpyTool("bash"));
        var engine = new DefaultPermissionEngine(
            [definition],
            new ClassDefaultPolicy(),
            new FixedConfirmation(answer: false));
        var loop = new AgentLoop(
            new OneToolClient(Cmd("bash", "rm -rf /")),
            [definition],
            permissionEngine: engine,
            toolExecutorFactory: executors);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<AgentEvent>();
        await foreach (var evt in loop.SubmitAsync("go", cts.Token))
            events.Add(evt);

        Assert.Equal(0, executors.Activations);
        Assert.Contains(events, e => e is AgentEvent.ToolDenied);
    }

    // ------------------------------------------------------------------
    // Backward compatibility: with NO engine configured the loop is unguarded — the
    // tool runs as it did pre-D5.
    // ------------------------------------------------------------------
    [Fact]
    public async Task NoEngine_Unguarded_ToolRuns()
    {
        var definition = Definition("bash", ToolAction.Execute);
        var executors = new CountingExecutorFactory(() => new SpyTool("bash"));
        var loop = new AgentLoop(
            new OneToolClient(Cmd("bash", "rm -rf /")),
            [definition],
            toolExecutorFactory: executors); // no engine

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var _ in loop.SubmitAsync("go", cts.Token)) { }

        Assert.Equal(1, executors.Activations); // unguarded: ran despite being an Execute
    }

    [Fact]
    public async Task Executor_IsActivatedOnlyWhenCalled_AndOncePerInvocation()
    {
        var definition = Definition("bash", ToolAction.Read);
        var executors = new CountingExecutorFactory(() => new SpyTool("bash"));
        var loop = new AgentLoop(
            new OneToolClient(Cmd("bash", "ls")),
            [definition],
            toolExecutorFactory: executors);

        Assert.Equal(0, executors.Activations);

        await foreach (var _ in loop.SubmitAsync("first")) { }
        await foreach (var _ in loop.SubmitAsync("second")) { }

        Assert.Equal(2, executors.Activations);
        Assert.Equal(2, executors.Instances.Count);
        Assert.NotSame(executors.Instances[0], executors.Instances[1]);
    }

    private sealed class CountingExecutorFactory(
        Func<SpyTool> factory) : IToolExecutorFactory
    {
        public List<SpyTool> Instances { get; } = [];
        public int Activations => Instances.Count;

        public IToolExecutor Create(string toolName)
        {
            var executor = factory();
            Instances.Add(executor);
            return executor;
        }
    }
}
