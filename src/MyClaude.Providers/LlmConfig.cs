namespace MyClaude.Providers;

public sealed class LlmConfig
{
    public required string Provider { get; init; }
    public required string Endpoint { get; init; }
    public required string ApiKey { get; init; }
    public required string DeploymentName { get; init; }
    public int MaxOutputTokens { get; init; } = 10_000;
}
