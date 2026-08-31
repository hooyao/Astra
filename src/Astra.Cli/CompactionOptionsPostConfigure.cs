using Astra.Core.Compaction;
using Astra.Providers;
using Microsoft.Extensions.Options;

namespace Astra.Cli;

internal sealed class CompactionOptionsPostConfigure(
    IOptions<LlmConfig> llmOptions) : IPostConfigureOptions<CompactionOptions>
{
    public void PostConfigure(string? name, CompactionOptions options)
    {
        options.MaxOutputTokens = llmOptions.Value.MaxOutputTokens;
        if (options.SummaryMaxOutputTokens <= 0)
            options.SummaryMaxOutputTokens = Math.Min(options.MaxOutputTokens, 20_000);

        options.CompactableToolNames = new HashSet<string>(
            options.CompactableToolNames ?? [],
            StringComparer.Ordinal);
    }
}
