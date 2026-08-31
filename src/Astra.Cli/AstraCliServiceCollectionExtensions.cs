using Astra.Cli.Tools;
using Astra.Core;
using Astra.Core.Compaction;
using Astra.Core.Coordination;
using Astra.Core.Files;
using Astra.Core.Permissions;
using Astra.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Astra.Cli;

public static class AstraCliServiceCollectionExtensions
{
    public static IServiceCollection AddAstraCli(
        this IServiceCollection services,
        IConfiguration configuration,
        IReadOnlyList<string> workspaceRoots)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(workspaceRoots);

        var configuredWorkspaceRoots = workspaceRoots.ToArray();
        var toolSection = configuration.GetRequiredSection(WorkspaceOptions.SectionName);

        services.AddOptions<LlmConfig>()
            .Bind(configuration.GetRequiredSection(LlmConfig.SectionName));
        services.AddSingleton<IValidateOptions<LlmConfig>, LlmConfigValidator>();

        services.AddOptions<WorkspaceOptions>()
            .Bind(toolSection)
            .PostConfigure(options =>
            {
                if (configuredWorkspaceRoots.Length == 0)
                    return;

                options.AllowedRoots = [.. configuredWorkspaceRoots];
                options.WorkingDirectory = configuredWorkspaceRoots[0];
            });
        services.AddOptions<PowerShellOptions>().Bind(toolSection);

        services.AddOptions<CompactionOptions>()
            .Bind(configuration.GetRequiredSection(CompactionOptions.SectionName));
        services.AddSingleton<IPostConfigureOptions<CompactionOptions>, CompactionOptionsPostConfigure>();
        services.AddSingleton<IValidateOptions<CompactionOptions>, CompactionOptionsValidator>();

        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IChatTokenEstimator, RoughChatTokenEstimator>();
        services.AddSingleton<WorkspaceFileSystem>();
        services.AddSingleton<CliToolCatalog>();

        // Every coordinator/worker scope owns a separate provider client. The
        // options-backed wrapper owns and disposes the selected provider adapter.
        services.AddScoped<IChatClient, ConfiguredChatClient>();
        services.AddScoped<UsageTrackingChatClient>();

        services.AddKeyedTransient<IToolExecutor, GetCurrentTimeTool>(GetCurrentTimeTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, ReadFileTool>(ReadFileTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, GlobTool>(GlobTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, GrepTool>(GrepTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, WriteFileTool>(WriteFileTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, EditFileTool>(EditFileTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, PowerShellTool>(PowerShellTool.ToolName);
        services.AddKeyedTransient<IToolExecutor, AgentTool>(AgentTool.ToolName);
        services.AddScoped<IToolExecutorFactory, DependencyInjectionToolExecutorFactory>();

        services.AddKeyedScoped<AgentLoop, WorkerAgentLoop>(AgentServiceKeys.WorkerLoop);
        services.AddScoped<IWorker, AgentLoopWorker>();
        services.AddSingleton<IWorkerSessionFactory, DependencyInjectionWorkerSessionFactory>();
        services.AddScoped<WorkerCoordinator>();

        services.AddSingleton<IPermissionPolicy, ClassDefaultPolicy>();
        services.AddSingleton<IUserConfirmation, ConsoleUserConfirmation>();
        services.AddScoped<IPermissionEngine, CoordinatorPermissionEngine>();
        services.AddScoped<IContextCompactor, ContextCompactor>();

        services.AddKeyedScoped<AgentLoop, MainAgentLoop>(AgentServiceKeys.MainLoop);
        services.AddScoped<AgentApp>();
        return services;
    }
}
