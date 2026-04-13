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

    public ValueTask<string> ExecuteAsync(IDictionary<string, object?>? arguments, CancellationToken ct)
    {
        return new(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
    }
}
