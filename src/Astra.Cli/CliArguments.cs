namespace Astra.Cli;

internal static class CliArguments
{
    public static IReadOnlyList<string> ReadWorkspaceRoots(string[] arguments)
    {
        const string option = "--workspace";
        var values = new List<string>();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], option, StringComparison.Ordinal))
            {
                if (i + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i + 1]))
                    throw new ArgumentException($"{option} requires a path.");
                values.Add(arguments[++i]);
                continue;
            }

            var prefix = option + "=";
            if (arguments[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                var value = arguments[i][prefix.Length..];
                if (string.IsNullOrWhiteSpace(value))
                    throw new ArgumentException($"{option} requires a path.");
                values.Add(value);
            }
        }

        return values;
    }
}
