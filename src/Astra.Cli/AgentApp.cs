using Astra.Core;
using Astra.Core.Coordination;
using Astra.Core.Files;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Cli;

/// <summary>
/// Console REPL — one of many possible consumers of AgentLoop's event stream.
/// The same AgentLoop can be driven by HTTP, WebSocket, or any other transport.
/// </summary>
public sealed class AgentApp(
    [FromKeyedServices(AgentServiceKeys.MainLoop)] AgentLoop loop,
    WorkspaceFileSystem fileSystem,
    WorkerCoordinator workerCoordinator)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Astra Agent");
        Console.WriteLine($"Working directory: {fileSystem.BaseDirectory}");
        Console.WriteLine($"File access: {fileSystem.AccessDescription}");
        Console.WriteLine("PowerShell: enabled; every command requires confirmation and is not constrained by file roots.");
        Console.WriteLine("Type a message to start, or 'exit' to quit.\n");

        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null or "exit") break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            try
            {
                await RunTurnAsync(loop, input, ct);

                while (workerCoordinator.HasOutstandingWork)
                {
                    var completions = await workerCoordinator.ReadUntilIdleAsync(ct);
                    if (completions.Count == 0)
                        break;

                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"  [workers: {completions.Count} completion(s), synthesizing]");
                    Console.ResetColor();
                    await RunTurnAsync(loop, WorkerCompletionXml.Serialize(completions), ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nError: {ex.Message}");
                Console.ResetColor();
            }

            Console.WriteLine();
        }
    }

    private static async Task RunTurnAsync(
        AgentLoop loop,
        string input,
        CancellationToken ct)
    {
        var needsNewline = false;
        await foreach (var evt in loop.SubmitAsync(input, ct))
        {
            switch (evt)
            {
                case AgentEvent.TextDelta { Text: var text }:
                    Console.Write(text);
                    needsNewline = true;
                    break;
                case AgentEvent.ToolUse { ToolName: var name }:
                    if (needsNewline) { Console.WriteLine(); needsNewline = false; }
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  [tool: {name}]");
                    Console.ResetColor();
                    break;
                case AgentEvent.ToolProgress { Text: var text }:
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  | {text}");
                    Console.ResetColor();
                    break;
                case AgentEvent.ToolFailure { Message: var message }:
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  [tool failure: {message}]");
                    Console.ResetColor();
                    break;
                case AgentEvent.ToolResult { Result: var result }:
                    var preview = result.Length > 200 ? result[..200] + "..." : result;
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  [result: {preview}]");
                    Console.ResetColor();
                    break;
                case AgentEvent.CompactionCompleted { Report: var report }:
                    if (needsNewline) { Console.WriteLine(); needsNewline = false; }
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine(
                        $"  [compact: {report.Trigger}, {report.TokensBefore:N0} -> {report.TokensAfter:N0} tokens, " +
                        $"steps={string.Join("+", report.Steps.Select(step => step.GetType().Name))}]");
                    Console.ResetColor();
                    break;
                case AgentEvent.Error { Message: var msg }:
                    if (needsNewline) { Console.WriteLine(); needsNewline = false; }
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Error: {msg}");
                    Console.ResetColor();
                    break;
            }
        }

        if (needsNewline)
            Console.WriteLine();
    }
}
