using Microsoft.Extensions.DependencyInjection;

namespace Astra.Core;

/// <summary>
/// Resolves keyed transient executors from the current agent or worker scope.
/// No executor is resolved until AgentLoop admits an actual tool call.
/// </summary>
public sealed class DependencyInjectionToolExecutorFactory(
    IServiceProvider serviceProvider) : IToolExecutorFactory
{
    public IToolExecutor Create(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return serviceProvider.GetRequiredKeyedService<IToolExecutor>(toolName);
    }
}
