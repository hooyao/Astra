using Microsoft.Extensions.Configuration;
using MyClaude.Core;
using MyClaude.Providers;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
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
var app = new AgentApp(chatClient);
await app.RunAsync(args);
