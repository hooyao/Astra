using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Astra.Core;

/// <summary>
/// A shell tool whose permission category varies by input — the canonical case
/// for "behavioral flags over inheritance". The same type returns
/// <see cref="ToolAction.Read"/> for "ls" and <see cref="ToolAction.Execute"/>
/// for "rm -rf": one tool, classification driven by the argument, not by a
/// subclass.
///
/// SCOPE (D2 v1): <see cref="Classify"/> recognizes a small, deliberately
/// incomplete set of command names. A production classifier is a per-command
/// allowlist engine — Claude Code's git-only read-only table is hundreds of
/// lines (utils/shell/readOnlyCommandValidation.ts). That belongs to a later
/// permission layer; here the goal is to prove the contract and the
/// input-dependence, not to be exhaustive.
/// TODO (permission layer): replace the name sets with a real allowlist engine
/// (flag/positional-aware, per-command), and classify compound commands by their
/// most-dangerous subcommand rather than the first token.
/// </summary>
public sealed class BashTool : ITool
{
    public string Name => "bash";

    public string Description =>
        "Run a shell command and return its combined stdout/stderr output.";

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "command": { "type": "string", "description": "The shell command to run." }
          },
          "required": ["command"]
        }
        """).RootElement.Clone();

    // --- D2 v1 classification tables -------------------------------------------------
    // Keyed by the command's first bare word. Names only; flags/positionals are a
    // later layer's job. Order of checks below encodes precedence (Execute wins).

    private static readonly HashSet<string> ReadCommands = new(StringComparer.Ordinal)
    {
        "ls", "cat", "pwd", "echo", "grep", "head", "tail", "find",
    };

    private static readonly HashSet<string> ExecuteCommands = new(StringComparer.Ordinal)
    {
        "rm", "mv", "dd", "mkfs",
    };

    private static readonly HashSet<string> WriteCommands = new(StringComparer.Ordinal)
    {
        "tee", "touch", "mkdir",
    };

    /// <summary>
    /// Classify the command by behavior. Input-dependent: this is the whole point
    /// of the contract. Precedence is most-dangerous-first so an ambiguous command
    /// never downgrades into a safer bucket.
    /// </summary>
    public ToolAction Classify(IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return ToolAction.Other; // fail closed: no command to reason about

        var word = FirstBareWord(command);
        if (word is null)
            return ToolAction.Other;

        // Output redirection writes to a file regardless of the verb ("echo x > f").
        // Checked before Read so "echo hi > file" is a Write, not a Read.
        // Heuristic only: ignores quoting/heredocs — a later layer parses properly.
        var redirectsToFile = command.Contains('>');

        // "mkfs.ext4" / "mkfs.xfs" are the same dangerous family as "mkfs". Exact
        // first-word matching alone misses these variants — the same brittleness
        // that makes Claude Code's exact/prefix command rules re-prompt on small
        // changes. A real classifier is a per-command allowlist; this one prefix-
        // matches just the mkfs family to make the limitation visible, not solved.
        if (ExecuteCommands.Contains(word) || word.StartsWith("mkfs.", StringComparison.Ordinal))
            return ToolAction.Execute;
        if (WriteCommands.Contains(word) || redirectsToFile)
            return ToolAction.Write;
        if (ReadCommands.Contains(word))
            return ToolAction.Read;

        return ToolAction.Other; // unrecognized → strictest bucket
    }

    public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
        IDictionary<string, object?>? arguments,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
        {
            yield return new ToolOutput.Result("Error: no 'command' argument provided.");
            yield break;
        }

        var (fileName, argsFlag) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c")
            : ("/bin/sh", "-c");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(argsFlag);
        psi.ArgumentList.Add(command);

        // Bridge the process's event-based output (OutputDataReceived) into a
        // pull-based async stream. The events push lines into an unbounded channel;
        // this method awaits the reader and yields each line as it arrives, so a
        // long-running command surfaces output live instead of after it exits.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false, // stdout and stderr handlers both write
        });

        // D4 (control layer): the child must be killed as a TREE on every abnormal
        // exit. `using` only Disposes the Process — that frees the OS handle and
        // sends NO signal, so on cancellation the spawned shell and its descendants
        // (`sh -c "npm install"` -> npm -> node -> ...) would be reparented to init
        // and keep running. The work always lives in the shell's descendants, never
        // in the shell itself, so a single Kill() of the shell is not enough —
        // entireProcessTree is the only correct option here. The kill goes in a
        // finally (below): it runs on normal completion, on a consumer break, and on
        // the OperationCanceledException thrown by ReadAllAsync / WaitForExitAsync.
        // try/finally with NO catch is legal around `yield` (CS1626 bans only catch),
        // and `await foreach` invokes this iterator's DisposeAsync on break/throw, so
        // the finally fires even when the consumer simply stops pulling.
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var full = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) channel.Writer.TryWrite(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) channel.Writer.TryWrite(e.Data); };
        // Complete the channel once the process exits AND both stream readers have
        // drained (Exited can fire before the last OutputDataReceived); WaitForExit
        // below flushes the async readers, so completing on Exited is safe here.
        process.Exited += (_, _) => channel.Writer.TryComplete();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            // Yield each line both as live Progress (to the human) and accumulate it
            // for the final Result (to the LLM). For bash the two coincide; the
            // contract does not require it.
            await foreach (var line in channel.Reader.ReadAllAsync(ct))
            {
                full.Append(line).Append('\n');
                yield return new ToolOutput.Progress(line);
            }

            await process.WaitForExitAsync(ct);
        }
        finally
        {
            await KillTreeAsync(process);
        }

        yield return new ToolOutput.Result(full.ToString().TrimEnd());
    }

    /// <summary>
    /// Kill the process and all its descendants, then reap it. A no-op if the
    /// process already exited (the normal-completion path), so it never false-kills.
    /// </summary>
    /// <remarks>
    /// Two races are swallowed by design. (1) TOCTOU: the process can exit between
    /// the <see cref="Process.HasExited"/> check and <see cref="Process.Kill(bool)"/>,
    /// which then throws <see cref="InvalidOperationException"/> — harmless, the
    /// goal (process gone) is already met. (2) Reaping uses
    /// <see cref="CancellationToken.None"/> on purpose: the caller's token is
    /// typically already cancelled (that is why we are killing), but we must still
    /// wait for the tree to actually tear down before returning, or we would hand
    /// back control while children are still alive.
    /// </remarks>
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
            return; // exited between the check and the kill — nothing left to reap
        }

        await process.WaitForExitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Pull the "command" string out of the weakly-typed argument bag. Isolated
    /// here so the stringly-typed access lives in exactly one place and both
    /// <see cref="Classify"/> and <see cref="ExecuteAsync"/> share it.
    /// </summary>
    private static string? GetCommand(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
            return null;
        if (!arguments.TryGetValue("command", out var raw) || raw is null)
            return null;
        // The bag may hold a JsonElement (from the wire) or a plain string (tests).
        return raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => raw.ToString(),
        };
    }

    /// <summary>
    /// First bare token of the command, skipping leading "VAR=value" environment
    /// assignments (so "FOO=bar rm x" classifies on "rm", not "FOO=bar"). Returns
    /// null if no command word is found. Heuristic; a later layer parses the AST.
    /// </summary>
    private static string? FirstBareWord(string command)
    {
        foreach (var token in command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Skip env-var assignment prefixes: NAME=value with NAME an identifier.
            var eq = token.IndexOf('=');
            if (eq > 0 && IsIdentifier(token.AsSpan(0, eq)))
                continue;
            return token;
        }
        return null;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty || !(char.IsLetter(s[0]) || s[0] == '_'))
            return false;
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        return true;
    }
}
