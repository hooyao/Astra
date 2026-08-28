using System.Globalization;
using System.Text.Json;

namespace Astra.Core.Files;

internal static class FileToolArguments
{
    public static string RequireString(
        IDictionary<string, object?>? arguments,
        string name)
    {
        var value = RequirePresentString(arguments, name);

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"Argument '{name}' must be a non-empty string.");

        return value;
    }

    public static string RequireNonEmptyString(
        IDictionary<string, object?>? arguments,
        string name)
    {
        var value = RequirePresentString(arguments, name);

        if (value.Length == 0)
            throw new ArgumentException($"Argument '{name}' must not be empty.");

        return value;
    }

    public static string OptionalString(
        IDictionary<string, object?>? arguments,
        string name,
        string fallback)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var raw) || raw is null)
            return fallback;

        return raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            _ => throw new ArgumentException($"Argument '{name}' must be a string."),
        };
    }

    public static string RequirePresentString(
        IDictionary<string, object?>? arguments,
        string name)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var raw) || raw is null)
            throw new ArgumentException($"Missing required '{name}' argument.");

        return raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
            _ => throw new ArgumentException($"Argument '{name}' must be a string."),
        };
    }

    public static int OptionalInt(
        IDictionary<string, object?>? arguments,
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var raw) || raw is null)
            return fallback;

        int? value = raw switch
        {
            int integer => integer,
            long integer when integer is >= int.MinValue and <= int.MaxValue => (int)integer,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var integer) => integer,
            JsonElement { ValueKind: JsonValueKind.String } json when
                int.TryParse(json.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            _ => null,
        };

        if (value is null || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Argument '{name}' must be between {minimum} and {maximum}.");

        return value.Value;
    }

    public static bool OptionalBool(
        IDictionary<string, object?>? arguments,
        string name,
        bool fallback)
    {
        if (arguments is null || !arguments.TryGetValue(name, out var raw) || raw is null)
            return fallback;

        return raw switch
        {
            bool boolean => boolean,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } json when bool.TryParse(json.GetString(), out var boolean) => boolean,
            string text when bool.TryParse(text, out var boolean) => boolean,
            _ => throw new ArgumentException($"Argument '{name}' must be a boolean."),
        };
    }
}
