using System.Diagnostics;
using Astra.Core;
using Astra.Core.Compaction;
using Astra.Core.Coordination;
using Astra.Core.Files;
using Astra.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

const string baselineLoopKey = "baseline";
const string coordinatorOnlyMarker = "D8-COORDINATOR-ONLY-7429";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== D8 Multi-Agent Coordinator Demo ===\n");

if (!args.Contains("--real", StringComparer.Ordinal))
{
    Console.WriteLine("This payoff compares real provider usage. Run:");
    Console.WriteLine("  dotnet run --project samples/MultiAgentDemo -- --real");
    Console.WriteLine("Optional: --root <Astra repository path>");
    return;
}

var root = Path.GetFullPath(ReadOption(args, "--root") ?? Directory.GetCurrentDirectory());
if (!Directory.Exists(root))
    throw new DirectoryNotFoundException(root);

var endpoint = Environment.GetEnvironmentVariable("ASTRA_LLM_ENDPOINT")
    ?? "http://localhost:8765/codex";
var model = Environment.GetEnvironmentVariable("ASTRA_LLM_MODEL")
    ?? "gpt-5.6-sol";
var apiKey = Environment.GetEnvironmentVariable("ASTRA_LLM_API_KEY")
    ?? string.Empty;
var services = new ServiceCollection();
services.AddOptions<LlmConfig>().Configure(options =>
{
    options.Provider = "OpenAIResponses";
    options.Endpoint = endpoint;
    options.DeploymentName = model;
    options.ApiKey = apiKey;
    options.MaxOutputTokens = 2_000;
});
services.AddSingleton<IValidateOptions<LlmConfig>, LlmConfigValidator>();
services.AddOptions<WorkspaceOptions>().Configure(options => options.WorkingDirectory = root);
services.AddSingleton<TimeProvider>(TimeProvider.System);
services.AddSingleton<IChatTokenEstimator, RoughChatTokenEstimator>();
services.AddSingleton<WorkspaceFileSystem>();
services.AddSingleton<DemoToolCatalog>();
services.AddScoped<IChatClient, ConfiguredChatClient>();
services.AddScoped<UsageTrackingChatClient>();
services.AddKeyedTransient<IToolExecutor, ReadFileTool>(ReadFileTool.ToolName);
services.AddKeyedTransient<IToolExecutor, GlobTool>(GlobTool.ToolName);
services.AddKeyedTransient<IToolExecutor, GrepTool>(GrepTool.ToolName);
services.AddKeyedTransient<IToolExecutor, AgentTool>(AgentTool.ToolName);
services.AddScoped<IToolExecutorFactory, DependencyInjectionToolExecutorFactory>();
services.AddKeyedScoped<AgentLoop, BaselineAgentLoop>(baselineLoopKey);
services.AddKeyedScoped<AgentLoop, DemoWorkerAgentLoop>(AgentServiceKeys.WorkerLoop);
services.AddScoped<IWorker, AgentLoopWorker>();
services.AddSingleton<IWorkerSessionFactory, DependencyInjectionWorkerSessionFactory>();
services.AddScoped<WorkerCoordinator>();
services.AddKeyedScoped<AgentLoop, DemoMainAgentLoop>(AgentServiceKeys.MainLoop);

await using var serviceProvider = services.BuildServiceProvider(
    new ServiceProviderOptions
    {
        ValidateOnBuild = true,
        ValidateScopes = true,
    });

Console.WriteLine($"Repository: {root}");
Console.WriteLine($"Model: {model}\n");

var baseline = await RunBaselineAsync(serviceProvider);
var multi = await RunMultiAgentAsync(serviceProvider);

Console.WriteLine("\n=== Visible comparison ===");
PrintUsage("single agent", baseline.Usage, baseline.ElapsedMilliseconds);
PrintUsage("coordinator", multi.CoordinatorUsage, multi.ElapsedMilliseconds);
Console.WriteLine(
    $"  workers:          tokens={multi.WorkerUsage.TotalTokens:N0}, " +
    $"cached={multi.WorkerUsage.CachedInputTokens:N0}, model_calls={multi.WorkerUsage.ModelCalls:N0}, " +
    $"tool_calls={multi.WorkerUsage.ToolCalls:N0}");
Console.WriteLine(
    $"  worker duration:  max={multi.Completions.Max(item => item.Usage.DurationMilliseconds):N0} ms, " +
    $"sum={multi.WorkerUsage.DurationMilliseconds:N0} ms");

var multiTokens = multi.CoordinatorUsage.TotalTokens + multi.WorkerUsage.TotalTokens;
if (baseline.Usage.TotalTokens > 0)
{
    Console.WriteLine(
        $"  token multiple:   {multiTokens / (double)baseline.Usage.TotalTokens:F2}x " +
        $"({multiTokens:N0} / {baseline.Usage.TotalTokens:N0})");
}
else
{
    Console.WriteLine("  token multiple:   unavailable (provider returned no baseline usage)");
}

Console.WriteLine($"  worker reports:   {multi.Completions.Count}");
Console.WriteLine(
    $"  isolation marker leaked into worker reports: " +
    $"{multi.NotificationXml.Contains(coordinatorOnlyMarker, StringComparison.Ordinal)}");

Console.WriteLine("\n=== Single-agent answer ===");
Console.WriteLine(baseline.Answer);
Console.WriteLine("\n=== Multi-agent synthesis ===");
Console.WriteLine(multi.Answer);

static async Task<RunResult> RunBaselineAsync(IServiceProvider rootProvider)
{
    Console.WriteLine("PART 1: single-agent baseline");
    await using var scope = rootProvider.CreateAsyncScope();
    var tracking = scope.ServiceProvider.GetRequiredService<UsageTrackingChatClient>();
    var loop = scope.ServiceProvider.GetRequiredKeyedService<AgentLoop>(baselineLoopKey);
    var stopwatch = Stopwatch.StartNew();
    var toolCalls = await RunTurnAsync(loop, ResearchQuestion(), "baseline");
    stopwatch.Stop();

    return new RunResult(
        tracking.LastResponseText,
        tracking.Snapshot(toolCalls, stopwatch.ElapsedMilliseconds),
        stopwatch.ElapsedMilliseconds);
}

static async Task<MultiRunResult> RunMultiAgentAsync(IServiceProvider rootProvider)
{
    Console.WriteLine("\nPART 2: coordinator plus two isolated workers");
    await using var scope = rootProvider.CreateAsyncScope();
    var coordinator = scope.ServiceProvider.GetRequiredService<WorkerCoordinator>();
    var tracking = scope.ServiceProvider.GetRequiredService<UsageTrackingChatClient>();
    var loop = scope.ServiceProvider.GetRequiredKeyedService<AgentLoop>(AgentServiceKeys.MainLoop);

    var stopwatch = Stopwatch.StartNew();
    var coordinatorToolCalls = await RunTurnAsync(
        loop,
        $"Coordinator-only marker: {coordinatorOnlyMarker}. Do not pass it to workers.\n\n{ResearchQuestion()}",
        "coordinator");

    if (!coordinator.HasOutstandingWork)
        throw new InvalidOperationException("Coordinator did not launch any workers.");

    var completions = await coordinator.ReadUntilIdleAsync();
    if (completions.Count != 2)
        throw new InvalidOperationException($"Expected 2 worker completions, received {completions.Count}.");
    foreach (var completion in completions)
    {
        Console.WriteLine(
            $"  worker {completion.WorkerId}: {completion.Status}, " +
            $"tokens={completion.Usage.TotalTokens:N0}, tools={completion.Usage.ToolCalls:N0}");
        if (completion.Status != WorkerStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Worker {completion.WorkerId} failed: {completion.Failure?.Message ?? completion.Status.ToString()}");
        }
    }

    var notificationXml = WorkerCompletionXml.Serialize(completions);
    coordinatorToolCalls += await RunTurnAsync(loop, notificationXml, "synthesis");
    stopwatch.Stop();

    return new MultiRunResult(
        tracking.LastResponseText,
        tracking.Snapshot(coordinatorToolCalls, stopwatch.ElapsedMilliseconds),
        SumUsage(completions.Select(completion => completion.Usage)),
        completions,
        notificationXml,
        stopwatch.ElapsedMilliseconds);
}

static async Task<int> RunTurnAsync(AgentLoop loop, string input, string label)
{
    var toolCalls = 0;
    await foreach (var evt in loop.SubmitAsync(
                       input,
                       new AgentTurnOptions { MaxOutputTokens = 2_000 }))
    {
        switch (evt)
        {
            case AgentEvent.ToolUse
            {
                ToolName: var toolName,
                CallId: var callId,
                Arguments: var arguments,
            }:
                toolCalls++;
                Console.WriteLine(
                    $"  {label} tool: {toolName} ({callId}) {FormatArguments(arguments)}");
                break;
            case AgentEvent.ToolFailure { Message: var message }:
                // Tool failures are also returned to the model as FunctionResultContent,
                // so the current turn can inspect the error and choose a corrected action.
                Console.WriteLine($"  {label} recoverable tool error: {message}");
                break;
            case AgentEvent.Error { Message: var message }:
                throw new InvalidOperationException($"{label} failed: {message}");
        }
    }
    return toolCalls;
}

static string FormatArguments(IDictionary<string, object?>? arguments)
{
    if (arguments is null || arguments.Count == 0)
        return "{}";

    const int maximumCharacters = 400;
    var rendered = string.Join(
        ", ",
        arguments.Select(pair =>
        {
            var value = pair.Value?.ToString() ?? "null";
            return $"{pair.Key}={value.Replace('\r', ' ').Replace('\n', ' ')}";
        }));
    return rendered.Length <= maximumCharacters
        ? $"{{{rendered}}}"
        : $"{{{rendered[..maximumCharacters]}...}}";
}

static WorkerUsage SumUsage(IEnumerable<WorkerUsage> usages)
{
    long input = 0;
    long output = 0;
    long cached = 0;
    long reasoning = 0;
    long total = 0;
    var modelCalls = 0;
    var toolCalls = 0;
    long duration = 0;
    var additional = new Dictionary<string, long>(StringComparer.Ordinal);

    foreach (var usage in usages)
    {
        input += usage.InputTokens;
        output += usage.OutputTokens;
        cached += usage.CachedInputTokens;
        reasoning += usage.ReasoningTokens;
        total += usage.TotalTokens;
        modelCalls += usage.ModelCalls;
        toolCalls += usage.ToolCalls;
        duration += usage.DurationMilliseconds;
        foreach (var (name, count) in usage.AdditionalTokenCounts)
            additional[name] = additional.GetValueOrDefault(name) + count;
    }

    return new WorkerUsage(
        input,
        output,
        cached,
        reasoning,
        total,
        modelCalls,
        toolCalls,
        duration,
        additional);
}

static void PrintUsage(string label, WorkerUsage usage, long elapsedMilliseconds) =>
    Console.WriteLine(
        $"  {label,-16} tokens={usage.TotalTokens,8:N0}, cached={usage.CachedInputTokens,8:N0}, " +
        $"model_calls={usage.ModelCalls,2:N0}, tool_calls={usage.ToolCalls,2:N0}, wall={elapsedMilliseconds,6:N0} ms");

static string ResearchQuestion() =>
    "Inspect how Astra implements (1) context compaction before model calls and (2) permission enforcement before " +
    "tool execution. For each subsystem, identify the primary implementation path and one test that proves the key " +
    "ordering invariant. Then explain briefly how the two guards compose. File-tool relative paths resolve from the " +
    "Astra repository root; use paths such as 'src/Astra.Core/AgentLoop.cs' and never prefix them with " +
    "'agent/refs/Astra/'. Use exact file:line evidence and do not modify files.";

static string? ReadOption(string[] arguments, string option)
{
    for (var i = 0; i < arguments.Length - 1; i++)
        if (string.Equals(arguments[i], option, StringComparison.Ordinal))
            return arguments[i + 1];
    return null;
}

internal sealed record RunResult(string Answer, WorkerUsage Usage, long ElapsedMilliseconds);

internal sealed record MultiRunResult(
    string Answer,
    WorkerUsage CoordinatorUsage,
    WorkerUsage WorkerUsage,
    IReadOnlyList<WorkerCompletion> Completions,
    string NotificationXml,
    long ElapsedMilliseconds);

internal sealed class DemoToolCatalog
{
    public DemoToolCatalog(WorkspaceFileSystem fileSystem)
    {
        ResearchDefinitions =
        [
            ReadFileTool.CreateDefinition(fileSystem),
            GlobTool.CreateDefinition(fileSystem),
            GrepTool.CreateDefinition(fileSystem),
        ];
    }

    public IReadOnlyList<ToolDefinition> ResearchDefinitions { get; }
}

internal sealed class BaselineAgentLoop(
    UsageTrackingChatClient chatClient,
    DemoToolCatalog tools,
    IToolExecutorFactory toolExecutorFactory)
    : AgentLoop(
        chatClient,
        tools.ResearchDefinitions,
        "You are a read-only code researcher. File-tool relative paths resolve from the Astra repository root. " +
        "Discover files with Glob/Grep, use repository-relative paths without an agent/refs/Astra prefix, and answer " +
        "with concise file:line evidence.",
        toolExecutorFactory: toolExecutorFactory);

internal sealed class DemoWorkerAgentLoop(
    UsageTrackingChatClient chatClient,
    DemoToolCatalog tools,
    IToolExecutorFactory toolExecutorFactory)
    : AgentLoop(
        chatClient,
        tools.ResearchDefinitions,
        "You are an isolated read-only code researcher. You cannot see the coordinator conversation. " +
        "File-tool relative paths resolve from the Astra repository root. Discover files with Glob/Grep and use " +
        "repository-relative paths without an agent/refs/Astra prefix. Gather exact evidence, do not modify files, " +
        "and obey the worker-report contract.",
        toolExecutorFactory: toolExecutorFactory);

internal sealed class DemoMainAgentLoop(
    UsageTrackingChatClient chatClient,
    IToolExecutorFactory toolExecutorFactory)
    : AgentLoop(
        chatClient,
        [AgentTool.Definition],
        "You are a coordinator with no direct file tools. For the initial request, emit exactly two Agent calls " +
        "in one response: one worker investigates compaction and one investigates permissions. Their prompts must " +
        "be self-contained and must not contain the coordinator-only marker. After task-notification messages arrive, " +
        "synthesize both reports with concrete file:line evidence. Never claim you inspected files directly.",
        toolExecutorFactory: toolExecutorFactory);
