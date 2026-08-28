using System.Collections.Immutable;
using Microsoft.Extensions.Configuration;
using Astra.Cli;
using Astra.Cli.Tools;
using Astra.Core;
using Astra.Core.Compaction;
using Astra.Core.Files;
using Astra.Core.Permissions;
using Astra.Providers;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var llmConfig = new LlmConfig
{
    Provider = config["Llm:Provider"] ?? "AzureOpenAI",
    Endpoint = config["Llm:Endpoint"] ?? throw new InvalidOperationException("Missing config: Llm:Endpoint"),
    ApiKey = config["Llm:ApiKey"] ?? throw new InvalidOperationException("Missing config: Llm:ApiKey"),
    DeploymentName = config["Llm:DeploymentName"] ?? throw new InvalidOperationException("Missing config: Llm:DeploymentName"),
    MaxOutputTokens = int.TryParse(config["Llm:MaxOutputTokens"], out var max) ? max : 10_000,
};

using var chatClient = ChatClientFactory.Create(llmConfig);

var workspaceRestrictions = ReadOptions(args, "--workspace");
var workingDirectorySetting = workspaceRestrictions.FirstOrDefault()
    ?? config["Tools:WorkingDirectory"];
var workingDirectory = Path.GetFullPath(
    string.IsNullOrWhiteSpace(workingDirectorySetting)
        ? Directory.GetCurrentDirectory()
        : workingDirectorySetting);
var fileSystem = new WorkspaceFileSystem(
    workingDirectory,
    workspaceRestrictions.Count == 0 ? null : workspaceRestrictions);
ITool[] tools =
[
    new GetCurrentTimeTool(),
    new ReadFileTool(fileSystem),
    new GlobTool(fileSystem),
    new GrepTool(fileSystem),
    new WriteFileTool(fileSystem),
    new EditFileTool(fileSystem),
    new PowerShellTool(
        fileSystem.BaseDirectory,
        config["Tools:PowerShellExecutable"]),
];
var permissionEngine = new DefaultPermissionEngine(
    tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal),
    new ClassDefaultPolicy(),
    new ConsoleUserConfirmation());

IContextCompactor? contextCompactor = null;
if (bool.TryParse(config["Compaction:Enabled"], out var compactionEnabled) && compactionEnabled)
{
    contextCompactor = new ContextCompactor(
        chatClient,
        new RoughChatTokenEstimator(),
        new CompactionOptions
        {
            ContextWindowTokens = ReadInt(config, "Compaction:ContextWindowTokens", 200_000),
            MaxOutputTokens = llmConfig.MaxOutputTokens,
            AutoCompactBufferTokens = ReadInt(config, "Compaction:AutoCompactBufferTokens", 13_000),
            AutoCompactThresholdOverrideTokens = ReadNullableInt(config, "Compaction:AutoCompactThresholdOverrideTokens"),
            SummaryMaxOutputTokens = ReadInt(
                config,
                "Compaction:SummaryMaxOutputTokens",
                Math.Min(llmConfig.MaxOutputTokens, 20_000)),
            FixedInputTokens = ReadInt(config, "Compaction:FixedInputTokens", 0),
            KeepRecentToolResults = ReadInt(config, "Compaction:KeepRecentToolResults", 5),
            MinimumMicrocompactSavingsTokens = ReadInt(config, "Compaction:MinimumMicrocompactSavingsTokens", 10_000),
            PreserveRecentUserTurns = ReadInt(config, "Compaction:PreserveRecentUserTurns", 1),
            CompactableToolNames = config
                .GetSection("Compaction:CompactableToolNames")
                .GetChildren()
                .Select(child => child.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .ToImmutableHashSet(StringComparer.Ordinal),
        });
}

var app = new AgentApp(
    chatClient,
    tools,
    workingDirectory: fileSystem.BaseDirectory,
    fileAccessDescription: fileSystem.AccessDescription,
    permissionEngine: permissionEngine,
    contextCompactor: contextCompactor);
await app.RunAsync();

static int ReadInt(IConfiguration config, string key, int fallback) =>
    int.TryParse(config[key], out var value) ? value : fallback;

static int? ReadNullableInt(IConfiguration config, string key) =>
    int.TryParse(config[key], out var value) ? value : null;

static IReadOnlyList<string> ReadOptions(string[] arguments, string option)
{
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
