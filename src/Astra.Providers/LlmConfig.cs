namespace Astra.Providers;

public sealed class LlmConfig
{
    public const string SectionName = "Llm";

    public string Provider { get; set; } = "AzureOpenAI";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string DeploymentName { get; set; } = string.Empty;
    public int MaxOutputTokens { get; set; } = 10_000;
}
