using System.Text.Json;
using Astra.Core;

namespace Astra.Cli.Tools;

public sealed class GetCurrentTimeTool : ITool
{
    public string Name => "get_current_time";
    public string Description => "Get the current date and time in the local timezone.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse("""
        {
            "type": "object",
            "properties": {}
        }
        """).RootElement.Clone();

    public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Read;

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask; // async iterator with no awaits — suppress CS1998
        yield return new ToolOutput.Result(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
    }
}
