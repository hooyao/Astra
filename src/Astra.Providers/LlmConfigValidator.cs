using Microsoft.Extensions.Options;

namespace Astra.Providers;

public sealed class LlmConfigValidator : IValidateOptions<LlmConfig>
{
    public ValidateOptionsResult Validate(string? name, LlmConfig options)
    {
        if (string.IsNullOrWhiteSpace(options.Provider))
            return ValidateOptionsResult.Fail("Llm:Provider must not be blank.");
        if (options.Provider is not ("AzureOpenAI" or "OpenAIResponses"))
            return ValidateOptionsResult.Fail($"Llm:Provider '{options.Provider}' is not supported.");
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _))
            return ValidateOptionsResult.Fail("Llm:Endpoint must be an absolute URI.");
        if (string.IsNullOrWhiteSpace(options.DeploymentName))
            return ValidateOptionsResult.Fail("Llm:DeploymentName must not be blank.");
        if (options.MaxOutputTokens <= 0)
            return ValidateOptionsResult.Fail("Llm:MaxOutputTokens must be positive.");

        return ValidateOptionsResult.Success;
    }
}
