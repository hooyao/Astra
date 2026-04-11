using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace MyClaude.Providers;

public static class ChatClientFactory
{
    public static IChatClient Create(LlmConfig config) => config.Provider switch
    {
        "AzureOpenAI" => CreateAzureOpenAI(config),
        _ => throw new NotSupportedException($"Provider '{config.Provider}' is not supported."),
    };

    private static IChatClient CreateAzureOpenAI(LlmConfig config)
    {
        var client = new AzureOpenAIClient(
            new Uri(config.Endpoint),
            new AzureKeyCredential(config.ApiKey));

        // gpt-5.4-pro uses the Responses API, not Chat Completions
#pragma warning disable OPENAI001
        return client.GetResponsesClient().AsIChatClient(config.DeploymentName);
#pragma warning restore OPENAI001
    }
}
