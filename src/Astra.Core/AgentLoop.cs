using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Astra.Core;

/// <summary>
/// The core agent loop: call LLM -> execute tools -> repeat until done.
/// Yields <see cref="AgentEvent"/> items via IAsyncEnumerable for backpressure and composability.
/// </summary>
public sealed class AgentLoop(IChatClient chatClient, IReadOnlyList<ITool> tools, string? systemPrompt = null)
{
    private readonly Dictionary<string, ITool> _toolMap = tools.ToDictionary(t => t.Name);
    private readonly List<AITool> _aiTools = tools.Select(t => (AITool)new ToolAIFunction(t)).ToList();
    private readonly List<ChatMessage> _messages = systemPrompt is not null
        ? [new(ChatRole.System, systemPrompt)]
        : [];

    public async IAsyncEnumerable<AgentEvent> SubmitAsync(
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var options = new ChatOptions();
        if (_aiTools.Count > 0)
            options.Tools = _aiTools;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

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

            // Execute tools and collect results
            List<AIContent> resultContents = [];
            foreach (var call in toolCalls)
            {
                yield return new AgentEvent.ToolUse(call.Name, call.CallId, call.Arguments);

                string result;
                AgentEvent.Error? toolError = null;

                if (_toolMap.TryGetValue(call.Name, out var tool))
                {
                    // Stream the tool: forward every Progress chunk to the consumer
                    // live, keep the last Result as the block fed back to the LLM.
                    // yield-return cannot live inside a try/catch (CS1626), and we
                    // must NOT buffer Progress until completion (that would defeat
                    // streaming). So drive the enumerator by hand: each step's
                    // MoveNext/Current runs in the try, the yield happens outside it.
                    string? finalResult = null;
                    var enumerator = tool.ExecuteAsync(call.Arguments, ct).GetAsyncEnumerator(ct);
                    try
                    {
                        while (true)
                        {
                            ToolOutput? output;
                            try
                            {
                                if (!await enumerator.MoveNextAsync())
                                    break;
                                output = enumerator.Current;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                toolError = new AgentEvent.Error($"Tool '{call.Name}' failed: {ex.Message}", ex);
                                finalResult = $"Error: {ex.Message}";
                                break;
                            }

                            if (output is ToolOutput.Progress(var text))
                                yield return new AgentEvent.ToolProgress(call.Name, call.CallId, text);
                            else if (output is ToolOutput.Result(var resultText))
                                finalResult = resultText;
                        }
                    }
                    finally
                    {
                        await enumerator.DisposeAsync();
                    }

                    // A tool that streamed only Progress (no Result) is treated as empty.
                    result = finalResult ?? string.Empty;
                }
                else
                {
                    result = $"Error: unknown tool '{call.Name}'";
                }

                if (toolError is not null)
                    yield return toolError;

                resultContents.Add(new FunctionResultContent(call.CallId, result));
                yield return new AgentEvent.ToolResult(call.Name, call.CallId, result);
            }

            // Add tool results to conversation and loop
            _messages.Add(new ChatMessage(ChatRole.Tool, resultContents));
        }
    }

    /// <summary>
    /// Adapts an ITool to AIFunction for the M.E.AI wire protocol — advertisement only
    /// (Name/Description/JsonSchema serialized into the request). Execution is NOT routed
    /// through here: the agent loop dispatches tools manually so that permission checks,
    /// read/write partitioning, and compaction hooks all have a seam to attach to.
    /// </summary>
    private sealed class ToolAIFunction(ITool tool) : AIFunction
    {
        public override string Name => tool.Name;
        public override string Description => tool.Description;
        public override JsonElement JsonSchema => tool.InputSchema;

        // AIFunction forces this override, but Astra never uses SDK auto-invocation.
        // Fail closed: if a middleware ever wakes this path, surface it loudly rather
        // than silently bypassing the manual dispatch in SubmitAsync.
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "Tools are dispatched manually by AgentLoop; auto-invocation is intentionally disabled.");
    }
}
