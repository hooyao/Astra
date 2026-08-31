using System.Text.Json;
using Astra.Core;

namespace Astra.Cli.Tools;

public sealed class GetCurrentTimeTool : IToolExecutor
{
    public const string ToolName = "get_current_time";

    private static readonly JsonElement Schema = ToolSchema.Parse("""
        {
            "type": "object",
            "properties": {}
        }
        """);

    public static ToolDefinition Definition { get; } = new(
        ToolName,
        "Get the current date and time in the local timezone.",
        Schema,
        static _ => ToolAction.Read);

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // async iterator with no awaits — suppress CS1998
        yield return new ToolOutput.Result(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
    }
}
