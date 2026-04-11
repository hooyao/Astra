using Microsoft.Extensions.AI;

namespace MyClaude.Core;

public sealed class AgentApp(IChatClient chatClient)
{
    public async Task RunAsync(string[] args, CancellationToken ct = default)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("MyClaude Agent");
        Console.WriteLine("Type a message to start, or 'exit' to quit.\n");

        List<ChatMessage> messages = [new(ChatRole.System, "You are a helpful assistant.")];

        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null or "exit") break;
            if (string.IsNullOrWhiteSpace(input)) continue;

            messages.Add(new(ChatRole.User, input));

            try
            {
                var updates = new List<ChatResponseUpdate>();
                await foreach (var chunk in chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
                {
                    if (chunk.Text is { } text)
                        Console.Write(text);
                    updates.Add(chunk);
                }
                Console.WriteLine("\n");

                // Merge streaming chunks back into conversation history
                messages.AddMessages(updates);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nError: {ex.Message}");
            }
        }
    }
}
