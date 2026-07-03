using System.Diagnostics;
using System.Text;

namespace Astra.Core.Context;

/// <summary>
/// Layer b provider that captures a one-time git snapshot: current branch,
/// <c>git status --short</c>, and the last five commits. This is Astra's counterpart
/// to Claude Code's <c>getGitStatus()</c> (src/context.ts:36) — it runs the real
/// subprocess, then the string is memoized (see <see cref="MemoizedSessionContext"/>)
/// and reused every turn for the rest of the session.
/// </summary>
/// <remarks>
/// D6. The returned text opens with the same "snapshot in time" preamble Claude Code
/// uses. That sentence is not decoration: it tells the model the value is frozen so
/// it does not trust a stale branch/status, and it documents the accepted trade of
/// freshness for a byte-stable, cacheable prefix. If git is unavailable or the
/// directory is not a repo, this returns an empty string (the layer simply
/// contributes nothing) rather than throwing — b must never break assembly.
/// </remarks>
public sealed class GitStatusContextProvider(string? workingDirectory = null) : ISessionContextProvider
{
    private const int MaxStatusChars = 2000; // mirror context.ts MAX_STATUS_CHARS

    private readonly string _cwd = workingDirectory ?? Directory.GetCurrentDirectory();

    public async ValueTask<string> GetAsync(CancellationToken ct = default)
    {
        var isRepo = await RunGitAsync("rev-parse --is-inside-work-tree", ct);
        if (isRepo?.Trim() != "true")
            return string.Empty;

        var branch = (await RunGitAsync("rev-parse --abbrev-ref HEAD", ct))?.Trim() ?? "(unknown)";
        var status = (await RunGitAsync("status --short", ct))?.Trim() ?? string.Empty;
        var log = (await RunGitAsync("log --oneline -n 5", ct))?.Trim() ?? string.Empty;
        var user = (await RunGitAsync("config user.name", ct))?.Trim();

        if (status.Length > MaxStatusChars)
            status = status[..MaxStatusChars]
                + "\n... (truncated because it exceeds 2k characters. Run \"git status\" for the full output.)";

        var sb = new StringBuilder();
        sb.Append(
            "This is the git status at the start of the conversation. Note that this status "
            + "is a snapshot in time, and will not update during the conversation.\n\n");
        sb.Append($"Current branch: {branch}\n\n");
        if (!string.IsNullOrEmpty(user))
            sb.Append($"Git user: {user}\n\n");
        sb.Append($"Status:\n{(status.Length == 0 ? "(clean)" : status)}\n\n");
        sb.Append($"Recent commits:\n{log}");
        return sb.ToString();
    }

    /// <summary>
    /// Run one <c>git</c> subcommand to completion and return its stdout, or null on
    /// any failure (git missing, non-zero exit). Run-to-completion, not streamed: b
    /// runs once at session start, so there is no live-output requirement here — the
    /// streaming machinery in <see cref="BashTool"/> would be overkill.
    /// </summary>
    private async Task<string?> RunGitAsync(string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = _cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(a);

            using var p = new Process { StartInfo = psi };
            p.Start();
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // git absent / not a repo / any spawn failure -> layer contributes nothing
        }
    }
}
