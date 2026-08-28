using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Astra.Core;

/// <summary>
/// Run a command in a fresh local PowerShell process. This is intentionally a
/// distinct tool from <see cref="BashTool"/>: on Windows BashTool uses
/// <c>cmd.exe /c</c>, while this tool passes the command directly to PowerShell
/// through <see cref="ProcessStartInfo.ArgumentList"/> with no nested quoting.
/// </summary>
/// <remarks>
/// Every invocation classifies as <see cref="ToolAction.Execute"/>. Determining
/// whether arbitrary PowerShell is read-only requires parsing the PowerShell AST
/// plus command/module semantics; a string allowlist would be unsafe. The CLI's
/// permission engine therefore confirms every invocation.
///
/// Workspace roots constrain the dedicated file tools, not a general shell. An
/// approved PowerShell command can access anything permitted to the Astra process.
/// </remarks>
public sealed class PowerShellTool : ITool
{
    private readonly string _executable;
    private readonly string _workingDirectory;

    public PowerShellTool(string workingDirectory, string? executable = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _workingDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(_workingDirectory))
            throw new DirectoryNotFoundException($"PowerShell working directory does not exist: {_workingDirectory}");

        _executable = string.IsNullOrWhiteSpace(executable)
            ? FindDefaultExecutable()
            : executable;
    }

    public string Name => "powershell";

    public string Description =>
        $"Run a local PowerShell command in '{_workingDirectory}'. " +
        "This general shell is not constrained by file-tool workspace roots and always requires confirmation.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "PowerShell source text to execute." }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public ToolAction Classify(IDictionary<string, object?>? arguments) => ToolAction.Execute;

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
        {
            yield return new ToolOutput.Result("Error: no 'command' argument provided.");
            yield break;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        var channel = Channel.CreateUnbounded<ProcessLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();

        var pendingStreams = 2;
        void OnData(DataReceivedEventArgs e, bool isError)
        {
            if (e.Data is not null)
                channel.Writer.TryWrite(new ProcessLine(e.Data, isError));
            else if (Interlocked.Decrement(ref pendingStreams) == 0)
                channel.Writer.TryComplete();
        }

        process.OutputDataReceived += (_, e) => OnData(e, isError: false);
        process.ErrorDataReceived += (_, e) => OnData(e, isError: true);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(ct))
            {
                var rendered = line.IsError ? $"[stderr] {line.Text}" : line.Text;
                output.Append(rendered).Append('\n');
                yield return new ToolOutput.Progress(rendered);
            }

            await process.WaitForExitAsync(ct);
        }
        finally
        {
            await KillTreeAsync(process);
        }

        yield return new ToolOutput.Result(
            $"Exit code: {process.ExitCode}\n{output.ToString().TrimEnd()}".TrimEnd());
    }

    private static async Task KillTreeAsync(Process process)
    {
        if (process.HasExited)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }

    private static string? GetCommand(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue("command", out var raw) || raw is null)
            return null;

        return raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => raw.ToString(),
        };
    }

    private static string FindDefaultExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return "pwsh";

        var powerShell7 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell",
            "7",
            "pwsh.exe");
        return File.Exists(powerShell7) ? powerShell7 : "powershell.exe";
    }

    private sealed record ProcessLine(string Text, bool IsError);
}
