using Astra.Cli;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddJsonFile("appsettings.Development.json", optional: true)
    .Build();

var services = new ServiceCollection();
services.AddAstraCli(configuration, CliArguments.ReadWorkspaceRoots(args));

await using var serviceProvider = services.BuildServiceProvider(
    new ServiceProviderOptions
    {
        ValidateOnBuild = true,
        ValidateScopes = true,
    });
await using var coordinatorSession = serviceProvider.CreateAsyncScope();
await coordinatorSession.ServiceProvider.GetRequiredService<AgentApp>().RunAsync();
