using Microsoft.Extensions.Configuration;
using Astra.Cli;
using Astra.Cli.Tools;
using Astra.Core;
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

ITool[] tools = [new GetCurrentTimeTool()];
var app = new AgentApp(chatClient, tools);
await app.RunAsync();
