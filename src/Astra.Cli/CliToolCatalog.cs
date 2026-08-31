using Astra.Cli.Tools;
using Astra.Core;
using Astra.Core.Coordination;
using Astra.Core.Files;

namespace Astra.Cli;

internal sealed class CliToolCatalog
{
    public CliToolCatalog(WorkspaceFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        WorkerDefinitions =
        [
            GetCurrentTimeTool.Definition,
            ReadFileTool.CreateDefinition(fileSystem),
            GlobTool.CreateDefinition(fileSystem),
            GrepTool.CreateDefinition(fileSystem),
        ];
        CoordinatorDefinitions =
        [
            .. WorkerDefinitions,
            AgentTool.Definition,
            WriteFileTool.CreateDefinition(fileSystem),
            EditFileTool.CreateDefinition(fileSystem),
            PowerShellTool.CreateDefinition(fileSystem.BaseDirectory),
        ];
    }

    public IReadOnlyList<ToolDefinition> WorkerDefinitions { get; }
    public IReadOnlyList<ToolDefinition> CoordinatorDefinitions { get; }
}
