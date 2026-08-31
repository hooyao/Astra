using Astra.Core;
using Astra.Core.Compaction;
using Astra.Core.Coordination;
using Astra.Core.Permissions;
using Microsoft.Extensions.AI;

namespace Astra.Cli;

internal sealed class WorkerAgentLoop(
    UsageTrackingChatClient chatClient,
    CliToolCatalog tools,
    IToolExecutorFactory toolExecutorFactory)
    : AgentLoop(
        chatClient,
        tools.WorkerDefinitions,
        "You are an isolated read-only Astra worker. You cannot see the coordinator conversation. " +
        "Complete only the self-contained task you receive, gather concrete evidence with the available tools, " +
        "do not modify files, and follow the worker-report contract in the task.",
        toolExecutorFactory: toolExecutorFactory);

internal sealed class MainAgentLoop(
    IChatClient chatClient,
    CliToolCatalog tools,
    IPermissionEngine permissionEngine,
    IContextCompactor contextCompactor,
    IToolExecutorFactory toolExecutorFactory)
    : AgentLoop(
        chatClient,
        tools.CoordinatorDefinitions,
        "You are Astra, a coding agent. Use Glob and Grep to find files and text, Read to inspect exact content, " +
        "Edit for targeted changes to existing files, and Write only for new files or intentional complete replacements. " +
        "Use Agent for substantial independent read-only research. Workers cannot see this conversation, so every worker " +
        "prompt must be self-contained. Emit multiple independent Agent calls together for parallel execution. Worker " +
        "results arrive as task-notification user messages; synthesize their evidence instead of forwarding it verbatim.",
        permissionEngine: permissionEngine,
        contextCompactor: contextCompactor,
        toolExecutorFactory: toolExecutorFactory);
