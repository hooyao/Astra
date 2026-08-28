using Astra.Core;
using Astra.Core.Compaction;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;

namespace Astra.Cli;

/// <summary>
/// Console REPL — one of many possible consumers of AgentLoop's event stream.
/// The same AgentLoop can be driven by HTTP, WebSocket, or any other transport.
/// </summary>
public sealed class AgentApp(
    IChatClient chatClient,
    IReadOnlyList<ITool> tools,
    string? workingDirectory = null,
    string? fileAccessDescription = null,
    IPermissionEngine? permissionEngine = null,
    IContextCompactor? contextCompactor = null)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("Astra Agent");
        if (workingDirectory is not null)
            Console.WriteLine($"Working directory: {workingDirectory}");
        if (fileAccessDescription is not null)
            Console.WriteLine($"File access: {fileAccessDescription}");
        if (tools.Any(tool => tool.Name == "powershell"))
            Console.WriteLine("PowerShell: enabled; every command requires confirmation and is not constrained by file roots.");
        Console.WriteLine("Type a message to start, or 'exit' to quit.\n");

        var loop = new AgentLoop(
            chatClient,
            tools,
            "You are Astra, a coding agent. Use Glob and Grep to find files and text, Read to inspect exact content, " +
            "Edit for targeted changes to existing files, and Write only for new files or intentional complete replacements.",
            permissionEngine: permissionEngine,
            contextCompactor: contextCompactor);

        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null or "exit") break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            var needsNewline = false;
            try
            {
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
                        case AgentEvent.ToolResult { ToolName: var name, Result: var result }:
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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (needsNewline) Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\nError: {ex.Message}");
                Console.ResetColor();
            }

            if (needsNewline) Console.WriteLine();
            Console.WriteLine();
        }
    }
}
