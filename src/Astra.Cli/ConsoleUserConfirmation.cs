using System.Text.Json;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;

namespace Astra.Cli;

/// <summary>Interactive fail-closed confirmation for write-class tool calls.</summary>
public sealed class ConsoleUserConfirmation : IUserConfirmation
{
    public async Task<bool> ConfirmAsync(
        FunctionCallContent call,
        string message,
        CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine($"Permission required: {message}");
        if (TryGetDisplayArgument(call.Arguments, out var argument))
            Console.WriteLine($"  {call.Name}: {argument}");
        Console.Write("Allow? [y/N] ");
        Console.ResetColor();

        var answer = await Console.In.ReadLineAsync(ct);
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDisplayArgument(
        IDictionary<string, object?>? arguments,
        out string value)
    {
        value = string.Empty;
        if (arguments is null)
            return false;

        foreach (var name in new[] { "path", "command" })
        {
            if (!arguments.TryGetValue(name, out var raw) || raw is null)
                continue;

            value = raw switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
                _ => raw.ToString() ?? string.Empty,
            };
            return value.Length > 0;
        }

        return false;
    }
}
