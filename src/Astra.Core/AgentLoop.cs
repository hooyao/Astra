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
                    try
                    {
                        result = await tool.ExecuteAsync(call.Arguments, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result = $"Error: {ex.Message}";
                        toolError = new AgentEvent.Error($"Tool '{call.Name}' failed: {ex.Message}", ex);
                    }
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

    /// <summary>Adapts an ITool to AIFunction for the M.E.AI wire protocol.</summary>
    private sealed class ToolAIFunction(ITool tool) : AIFunction
    {
        public override string Name => tool.Name;
        public override string Description => tool.Description;
        public override JsonElement JsonSchema => tool.InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            return await tool.ExecuteAsync(arguments, cancellationToken);
        }
    }
}
