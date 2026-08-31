using Astra.Core.Permissions;

namespace Astra.Cli;

internal sealed class CoordinatorPermissionEngine(
    CliToolCatalog tools,
    IPermissionPolicy policy,
    IUserConfirmation confirmation)
    : DefaultPermissionEngine(tools.CoordinatorDefinitions, policy, confirmation);
