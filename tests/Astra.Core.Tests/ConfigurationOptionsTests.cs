using Astra.Cli;
using Astra.Core;
using Astra.Core.Compaction;
using Astra.Core.Files;
using Astra.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Astra.Core.Tests;

public sealed class ConfigurationOptionsTests : IDisposable
{
    private readonly string _configuredRoot = Path.Combine(
        Path.GetTempPath(),
        $"AstraConfiguredRoot-{Guid.NewGuid():N}");
    private readonly string _commandLineRoot = Path.Combine(
        Path.GetTempPath(),
        $"AstraCommandLineRoot-{Guid.NewGuid():N}");

    public ConfigurationOptionsTests()
    {
        Directory.CreateDirectory(_configuredRoot);
        Directory.CreateDirectory(_commandLineRoot);
    }

    [Fact]
    public async Task AddAstraCli_BindsValidatesAndInjectsStronglyTypedOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "OpenAIResponses",
                ["Llm:Endpoint"] = "http://localhost:8765/codex",
                ["Llm:ApiKey"] = string.Empty,
                ["Llm:DeploymentName"] = "gpt-5.6-sol",
                ["Llm:MaxOutputTokens"] = "4321",
                ["Tools:WorkingDirectory"] = _configuredRoot,
                ["Tools:PowerShellExecutable"] = "test-pwsh",
                ["Compaction:Enabled"] = "false",
                ["Compaction:ContextWindowTokens"] = "1000000",
                ["Compaction:AutoCompactBufferTokens"] = "13000",
                ["Compaction:SummaryMaxOutputTokens"] = "2000",
                ["Compaction:CompactableToolNames:0"] = "Read",
                ["Compaction:CompactableToolNames:1"] = "Grep",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAstraCli(configuration, [_commandLineRoot]);

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var llm = provider.GetRequiredService<IOptions<LlmConfig>>().Value;
        Assert.Equal("gpt-5.6-sol", llm.DeploymentName);
        Assert.Equal(4_321, llm.MaxOutputTokens);

        var compaction = provider.GetRequiredService<IOptions<CompactionOptions>>().Value;
        Assert.False(compaction.Enabled);
        Assert.Equal(llm.MaxOutputTokens, compaction.MaxOutputTokens);
        Assert.Equal(["Grep", "Read"], compaction.CompactableToolNames.Order());

        var powerShell = provider.GetRequiredService<IOptions<PowerShellOptions>>().Value;
        Assert.Equal("test-pwsh", powerShell.PowerShellExecutable);

        var fileSystem = provider.GetRequiredService<WorkspaceFileSystem>();
        Assert.Equal(Path.GetFullPath(_commandLineRoot), fileSystem.BaseDirectory);
        Assert.True(fileSystem.IsRestricted);

        await using var scope = provider.CreateAsyncScope();
        var executorFactory = scope.ServiceProvider.GetRequiredService<IToolExecutorFactory>();
        Assert.IsType<PowerShellTool>(executorFactory.Create(PowerShellTool.ToolName));

        var contextCompactor = scope.ServiceProvider.GetRequiredService<IContextCompactor>();
        var result = await contextCompactor.CompactIfNeededAsync(
            [new ChatMessage(ChatRole.User, new string('x', 10_000))],
            CompactionTrigger.Automatic,
            CancellationToken.None);
        var notNeeded = Assert.IsType<CompactionResult.NotNeeded>(result);
        Assert.Equal(0, notNeeded.InputTokens);
    }

    [Fact]
    public async Task InvalidLlmOptions_FailWhenTheOptionsBackedClientIsResolved()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:Provider"] = "OpenAIResponses",
                ["Llm:Endpoint"] = "not-an-absolute-uri",
                ["Llm:DeploymentName"] = "gpt-5.6-sol",
                ["Tools:WorkingDirectory"] = _configuredRoot,
                ["Compaction:Enabled"] = "false",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddAstraCli(configuration, []);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var error = Assert.Throws<OptionsValidationException>(
            () => scope.ServiceProvider.GetRequiredService<IChatClient>());

        Assert.Contains("absolute URI", error.Message);
    }

    public void Dispose()
    {
        Directory.Delete(_configuredRoot, recursive: true);
        Directory.Delete(_commandLineRoot, recursive: true);
    }
}
