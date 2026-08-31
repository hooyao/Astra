using Astra.Core;
using Xunit;

namespace Astra.Core.Tests;

public class PowerShellToolTests
{
    private static IDictionary<string, object?> Command(string command) =>
        new Dictionary<string, object?> { ["command"] = command };

    [Fact]
    public async Task ExecuteAsync_StreamsStdoutAndStderr_AndReportsExitCode()
    {
        var tool = new PowerShellTool(Path.GetTempPath());
        var outputs = new List<ToolOutput>();

        await foreach (var output in tool.ExecuteAsync(
            Command("Write-Output 'alpha'; [Console]::Error.WriteLine('beta'); exit 7"),
            CancellationToken.None))
        {
            outputs.Add(output);
        }

        Assert.Equal(
            ToolAction.Execute,
            PowerShellTool.CreateDefinition(Path.GetTempPath()).Classify(null));
        Assert.Contains(
            outputs.OfType<ToolOutput.Progress>(),
            output => output.Text == "alpha");
        Assert.Contains(
            outputs.OfType<ToolOutput.Progress>(),
            output => output.Text == "[stderr] beta");

        var result = Assert.Single(outputs.OfType<ToolOutput.Result>());
        Assert.Contains("Exit code: 7", result.Text);
        Assert.Contains("alpha", result.Text);
        Assert.Contains("[stderr] beta", result.Text);
    }

    [Fact]
    public async Task ExecuteAsync_MissingCommand_ReturnsErrorResult()
    {
        var tool = new PowerShellTool(Path.GetTempPath());
        var outputs = new List<ToolOutput>();

        await foreach (var output in tool.ExecuteAsync(
            new Dictionary<string, object?>(),
            CancellationToken.None))
            outputs.Add(output);

        var result = Assert.IsType<ToolOutput.Result>(Assert.Single(outputs));
        Assert.Contains("no 'command'", result.Text);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_KillsProcessPromptly()
    {
        var tool = new PowerShellTool(Path.GetTempPath());
        using var cts = new CancellationTokenSource();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var run = Task.Run(async () =>
        {
            await foreach (var _ in tool.ExecuteAsync(
                Command("Write-Output 'started'; Start-Sleep -Seconds 30"),
                cts.Token))
            {
            }
        });

        await Task.Delay(300);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Cancellation took {stopwatch.Elapsed.TotalSeconds:F1}s instead of terminating promptly.");
    }
}
