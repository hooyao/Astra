using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Astra.Core.Compaction;
using Astra.Core.Context;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;

namespace Astra.Core;

/// <summary>
/// The core agent loop: call LLM -> execute tools -> repeat until done.
/// Yields <see cref="AgentEvent"/> items via IAsyncEnumerable for backpressure and composability.
/// </summary>
/// <remarks>
/// D6 (context assembly) layers three lifetimes into what gets sent each turn:
/// <list type="bullet">
/// <item><b>a</b> — <paramref name="systemPrompt"/>: static identity, never changes.</item>
/// <item><b>b</b> — <paramref name="sessionContext"/>: computed once at session start and
/// frozen (memoize it with <see cref="MemoizedSessionContext"/>). Appended after a to
/// form a single byte-stable system prefix, so a provider's prompt cache keeps hitting it.</item>
/// <item><b>c</b> — <paramref name="attachmentProviders"/>: recomputed every turn under
/// <paramref name="attachmentDeadline"/> and appended to the current user message. A hung
/// provider is dropped at the deadline so it cannot delay the turn.</item>
/// </list>
/// All three are optional; passing none reproduces the pre-D6 behavior (bare system
/// prompt, no attachments), so existing callers/tests are unaffected.
/// See agent/experiments/d06-context-assembly/source-reconciliation.md.
/// </remarks>
public class AgentLoop(
    IChatClient chatClient,
    IReadOnlyList<ToolDefinition> toolDefinitions,
    string? systemPrompt = null,
    IPermissionEngine? permissionEngine = null,
    ISessionContextProvider? sessionContext = null,
    IReadOnlyList<IAttachmentProvider>? attachmentProviders = null,
    TimeSpan? attachmentDeadline = null,
    IContextCompactor? contextCompactor = null,
    IToolExecutorFactory? toolExecutorFactory = null)
{
    /// <summary>
    /// Upper bound on tools running at once inside a single concurrent (read) batch.
    /// Claude Code uses the same default (10, env CLAUDE_CODE_MAX_TOOL_USE_CONCURRENCY).
    /// A bound matters because the model can request many reads in one turn and each
    /// is a real process / file handle.
    /// </summary>
    private const int MaxConcurrentTools = 10;

    private readonly Dictionary<string, ToolDefinition> _toolMap =
        toolDefinitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);
    private readonly List<AITool> _aiTools =
        toolDefinitions.Select(definition => (AITool)new ToolAIFunction(definition)).ToList();
    private readonly IToolExecutorFactory? _toolExecutorFactory =
        RequireExecutorFactory(toolDefinitions, toolExecutorFactory);

    // Layer c: the per-turn attachment gatherer (null when no providers were supplied).
    // Default deadline mirrors Claude Code's getAttachments() 1-second AbortController.
    private readonly AttachmentGatherer? _attachments =
        attachmentProviders is { Count: > 0 }
            ? new AttachmentGatherer(attachmentProviders, attachmentDeadline ?? TimeSpan.FromSeconds(1))
            : null;

    // The conversation transcript. The system message (layers a + b) is prepended
    // once, lazily, on the first SubmitAsync — b's provider is async so it cannot run
    // in the constructor. After that the system message is never rebuilt, which is
    // what keeps the a+b prefix byte-stable across turns.
    private List<ChatMessage> _messages = [];
    private bool _systemAssembled;

    /// <summary>
    /// Build the system message (a + b) exactly once and prepend it. b is awaited here,
    /// on the first turn only; memoize the provider so this single await is also the
    /// only time its underlying work (e.g. a git subprocess) runs.
    /// </summary>
    private async ValueTask EnsureSystemAssembledAsync(CancellationToken ct)
    {
        if (_systemAssembled)
            return;
        _systemAssembled = true;

        var a = systemPrompt;
        var b = sessionContext is not null ? await sessionContext.GetAsync(ct) : null;

        var prefix = (a, b) switch
        {
            (null, null) => null,
            (not null, null) => a,
            (null, not null) => b,
            (not null, not null) => $"{a}\n\n{b}",
        };

        if (prefix is not null)
            _messages.Insert(0, new ChatMessage(ChatRole.System, prefix));
    }

    public IAsyncEnumerable<AgentEvent> SubmitAsync(
        string userMessage,
        CancellationToken ct = default) =>
        SubmitCoreAsync(userMessage, turnOptions: null, ct);

    public IAsyncEnumerable<AgentEvent> SubmitAsync(
        string userMessage,
        AgentTurnOptions turnOptions,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turnOptions);
        turnOptions.Validate();
        return SubmitCoreAsync(userMessage, turnOptions, ct);
    }

    private async IAsyncEnumerable<AgentEvent> SubmitCoreAsync(
        string userMessage,
        AgentTurnOptions? turnOptions,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Layer a + b: assemble the byte-stable system prefix once, before the first turn.
        await EnsureSystemAssembledAsync(ct);

        // Layer c: gather this turn's attachments under the deadline and append them
        // to the user message. Attachments precede the user's text (they are ambient
        // context the model reads before the ask), each in a labeled block. A dropped
        // or timed-out provider simply contributes nothing this turn.
        var userContent = userMessage;
        if (_attachments is not null)
        {
            var gathered = await _attachments.GatherAsync(ct);
            if (gathered.Count > 0)
            {
                var blocks = gathered.Select(att => $"<attachment name=\"{att.Name}\">\n{att.Text}\n</attachment>");
                userContent = $"{string.Join("\n\n", blocks)}\n\n{userMessage}";
            }
        }

        _messages.Add(new ChatMessage(ChatRole.User, userContent));

        var options = new ChatOptions { MaxOutputTokens = turnOptions?.MaxOutputTokens };
        if (_aiTools.Count > 0)
            options.Tools = _aiTools;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // D7 — preflight before EVERY model round-trip, not merely once per
            // SubmitAsync call. A tool result appended later in this same turn may
            // itself cross the context threshold. Only Applied exposes a detached,
            // complete candidate; assignment commits it atomically. Failed carries
            // no candidate, so the original history remains authoritative.
            if (contextCompactor is not null)
            {
                var compaction = await contextCompactor.CompactIfNeededAsync(
                    _messages,
                    CompactionTrigger.Automatic,
                    ct);

                switch (compaction)
                {
                    case CompactionResult.NotNeeded:
                        break;

                    case CompactionResult.Applied applied:
                        _messages = [.. applied.CandidateMessages];
                        yield return new AgentEvent.CompactionCompleted(applied.Report);
                        break;

                    case CompactionResult.Failed failed:
                        yield return new AgentEvent.Error(
                            $"Context compaction failed ({failed.Failure.Kind}): {failed.Failure.Message}");
                        yield break;
                }
            }

            // Stream the response — text deltas go to the consumer immediately
            var updates = new List<ChatResponseUpdate>();
            await foreach (var chunk in chatClient.GetStreamingResponseAsync(_messages, options, ct))
            {
                updates.Add(chunk);
                if (chunk.Text is { Length: > 0 } text)
                    yield return new AgentEvent.TextDelta(text);
            }

            // Build complete response for tool detection
            var response = updates.ToChatResponse();
            _messages.AddMessages(response);

            if (response.Messages.Count == 0)
                yield break;

            // Check for tool calls
            var lastMessage = response.Messages[^1];
            var toolCalls = lastMessage.Contents
                .OfType<FunctionCallContent>()
                .ToList();

            if (toolCalls.Count == 0)
                yield break; // No tool calls — LLM is done

            // D3 — partition this turn's calls into batches: runs of read-only
            // calls that may execute concurrently, and single non-read calls that
            // act as barriers and run alone. See ToolBatching for the why (it is a
            // reordering / data-hazard problem, not a sort).
            var batches = ToolBatching.Partition(toolCalls, ClassifyCall);

            // One Tool message carries every result for the turn (as in D2). We
            // accumulate across batches and add it once, after all batches run.
            List<AIContent> resultContents = [];

            foreach (var batch in batches)
            {
                // Announce every call in the batch up front, in the model's order —
                // deterministic regardless of which tool finishes first.
                foreach (var call in batch.Calls)
                    yield return new AgentEvent.ToolUse(call.Name, call.CallId, call.Arguments);

                // The batch runs in a background producer that owns all try/catch and
                // writes events into a channel. The iterator just drains the channel
                // and yields — which keeps the yield out of any try/catch (CS1626) and
                // merges N concurrent tool streams into one ordered event stream.
                var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false, // concurrent tools all write
                });
                var results = new ConcurrentDictionary<string, AIContent>();
                var producer = RunBatchAsync(batch, channel.Writer, results, ct);

                // Drain without a ct: the producer's finally always completes the
                // channel (even on cancellation), so the drain ends and we then
                // observe the producer task to surface any OperationCanceledException.
                await foreach (var evt in channel.Reader.ReadAllAsync(CancellationToken.None))
                    yield return evt;

                await producer; // re-throws cancellation; tool failures became Error events

                // Feed results back to the LLM in the model's original call order
                // (the concurrent batch may have completed them out of order).
                foreach (var call in batch.Calls)
                    if (results.TryGetValue(call.CallId, out var content))
                        resultContents.Add(content);
            }

            // Add tool results to conversation and loop
            _messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
        }
    }

    /// <summary>
    /// Concurrency-safety is derived from D2's behavioral classification — there is
    /// no separate flag. A call is safe to run in parallel iff it is a pure read;
    /// an unknown tool classifies as <see cref="ToolAction.Other"/> (fail-closed),
    /// so it runs alone.
    /// </summary>
    private ToolAction ClassifyCall(FunctionCallContent call) =>
        _toolMap.TryGetValue(call.Name, out var definition)
            ? definition.Classify(call.Arguments)
            : ToolAction.Other;

    /// <summary>
    /// Run one batch to completion, writing all of its events into <paramref name="writer"/>
    /// and depositing each call's final result into <paramref name="results"/> keyed by
    /// CallId. A concurrent batch runs its calls in parallel up to
    /// <see cref="MaxConcurrentTools"/>; a serial batch (always a single non-read call)
    /// runs that one call. The channel is always completed in the finally so the
    /// iterator's drain terminates even on cancellation or fault.
    /// </summary>
    private async Task RunBatchAsync(
        ToolBatch batch,
        ChannelWriter<AgentEvent> writer,
        ConcurrentDictionary<string, AIContent> results,
        CancellationToken ct)
    {
        try
        {
            var maxConcurrency = batch.IsConcurrent ? MaxConcurrentTools : 1;
            using var slots = new SemaphoreSlim(maxConcurrency);
            var tasks = batch.Calls.Select(async call =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    await RunOneToolAsync(call, writer, results, ct);
                }
                finally
                {
                    slots.Release();
                }
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            writer.Complete();
        }
    }

    /// <summary>
    /// Execute a single tool call, streaming its <see cref="ToolOutput.Progress"/> live
    /// as <see cref="AgentEvent.ToolProgress"/> and recording its single
    /// <see cref="ToolOutput.Result"/> as the block fed back to the LLM. This is a plain
    /// async method (not an iterator), so the try/catch around the stream is allowed.
    /// Cancellation propagates; any other tool failure becomes an Error event plus an
    /// error result string (so the LLM sees the failure rather than the turn vanishing).
    /// </summary>
    private async Task RunOneToolAsync(
        FunctionCallContent call,
        ChannelWriter<AgentEvent> writer,
        ConcurrentDictionary<string, AIContent> results,
        CancellationToken ct)
    {
        if (!_toolMap.TryGetValue(call.Name, out var definition))
        {
            var unknown = $"Error: unknown tool '{call.Name}'";
            results[call.CallId] = new FunctionResultContent(call.CallId, unknown);
            await writer.WriteAsync(new AgentEvent.ToolResult(call.Name, call.CallId, unknown), CancellationToken.None);
            return;
        }

        // Permission gate (D5) — runs BEFORE ExecuteAsync, so a denied side effect
        // never happens. When no engine is configured the loop is unguarded (every
        // call allowed), preserving the pre-D5 behavior. A Deny short-circuits: its
        // reason becomes the tool result fed back to the LLM (so the model adapts)
        // plus a ToolDenied event for the human. Cancellation during an interactive
        // Ask propagates like any other OCE.
        IDictionary<string, object?>? effectiveArgs = call.Arguments;
        if (permissionEngine is not null)
        {
            var decision = await permissionEngine.CheckAsync(call, definition.Classify(call.Arguments), ct);
            if (decision is PermissionDecision.Deny(var reason))
            {
                results[call.CallId] = new FunctionResultContent(call.CallId, reason);
                await writer.WriteAsync(new AgentEvent.ToolDenied(call.Name, call.CallId, reason), CancellationToken.None);
                await writer.WriteAsync(new AgentEvent.ToolResult(call.Name, call.CallId, reason), CancellationToken.None);
                return;
            }
            if (decision is PermissionDecision.Allow(var updated) && updated is not null)
                effectiveArgs = updated; // a policy may rewrite arguments before run
        }

        string? finalResult = null;
        try
        {
            // Activation is deliberately after permission. Unused, unknown, and
            // denied tools never create executor instances.
            var executor = _toolExecutorFactory!.Create(call.Name);
            await foreach (var output in executor.ExecuteAsync(effectiveArgs, ct).WithCancellation(ct))
            {
                if (output is ToolOutput.Progress(var text))
                    await writer.WriteAsync(new AgentEvent.ToolProgress(call.Name, call.CallId, text), ct);
                else if (output is ToolOutput.Result(var resultText))
                    finalResult = resultText;
            }
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a tool result — let it propagate
        }
        catch (Exception ex)
        {
            await writer.WriteAsync(
                new AgentEvent.Error($"Tool '{call.Name}' failed: {ex.Message}", ex), CancellationToken.None);
            var errMsg = $"Error: {ex.Message}";
            results[call.CallId] = new FunctionResultContent(call.CallId, errMsg);
            await writer.WriteAsync(new AgentEvent.ToolResult(call.Name, call.CallId, errMsg), CancellationToken.None);
            return;
        }

        // A tool that streamed only Progress (no Result) is treated as empty.
        var result = finalResult ?? string.Empty;
        results[call.CallId] = new FunctionResultContent(call.CallId, result);
        await writer.WriteAsync(new AgentEvent.ToolResult(call.Name, call.CallId, result), ct);
    }

    /// <summary>
    /// Adapts immutable metadata to AIFunction for the M.E.AI wire protocol.
    /// (Name/Description/JsonSchema serialized into the request). Execution is NOT routed
    /// through here: the agent loop dispatches tools manually so that permission checks,
    /// read/write partitioning, and compaction hooks all have a seam to attach to.
    /// </summary>
    private sealed class ToolAIFunction(ToolDefinition definition) : AIFunction
    {
        public override string Name => definition.Name;
        public override string Description => definition.Description;
        public override JsonElement JsonSchema => definition.InputSchema;

        // AIFunction forces this override, but Astra never uses SDK auto-invocation.
        // Fail closed: if a middleware ever wakes this path, surface it loudly rather
        // than silently bypassing the manual dispatch in SubmitAsync.
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Tools are dispatched manually by AgentLoop; auto-invocation is intentionally disabled.");
    }

    private static IToolExecutorFactory? RequireExecutorFactory(
        IReadOnlyList<ToolDefinition> definitions,
        IToolExecutorFactory? factory)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count > 0 && factory is null)
        {
            throw new ArgumentNullException(
                nameof(toolExecutorFactory),
                "A tool executor factory is required when tool definitions are advertised.");
        }

        return factory;
    }
}
