using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace Astra.Providers;

public static class ChatClientFactory
{
    public static IChatClient Create(LlmConfig config) => config.Provider switch
    {
        "AzureOpenAI" => CreateAzureOpenAI(config),
        "OpenAIResponses" => CreateOpenAIResponses(config),
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

    private static IChatClient CreateOpenAIResponses(LlmConfig config)
    {
        // The OpenAI SDK requires a non-empty credential even when a local
        // compatible endpoint performs no authentication. The placeholder is
        // transport-only and carries no authority at such an endpoint.
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(config.ApiKey) ? "not-required" : config.ApiKey);
        var endpoint = config.Endpoint.EndsWith('/')
            ? new Uri(config.Endpoint)
            : new Uri(config.Endpoint + "/");
        var client = new OpenAIClient(
            credential,
            new OpenAIClientOptions { Endpoint = endpoint });

#pragma warning disable OPENAI001
        return client.GetResponsesClient().AsIChatClient(config.DeploymentName);
#pragma warning restore OPENAI001
    }
}
