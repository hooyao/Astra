using Astra.Core;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D4 (control layer) — cancelling a BashTool invocation must kill the spawned
/// shell AND its descendants, then reap, so "stop" means the work actually stops
/// rather than "I stop watching it". The proof is by construction: a grandchild
/// subshell ticks a marker file forever; if only the shell were killed, the
/// reparented grandchild would keep ticking. See
/// agent/experiments/d04-control-layer/teaching-notes.md (parent repo).
/// </summary>
public class BashToolCancellationTests
{
    private static IDictionary<string, object?> Cmd(string command) =>
        new Dictionary<string, object?> { ["command"] = command };

    // Read a file that a separate process may be appending to right now: share
    // read+write so we never collide with the grandchild's append-mode open.
    private static int CountLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var n = 0;
        while (reader.ReadLine() is not null) n++;
        return n;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
                throw new TimeoutException("condition was not met within the timeout");
            await Task.Delay(50);
        }
    }

    // ------------------------------------------------------------------
    // [the load-bearing D4 test] Cancel a tool whose shell has spawned a
    // background grandchild. The whole TREE must die: after the kill the marker
    // file stops growing. A single Kill() of the shell (no tree) would leave the
    // grandchild running and ticking — this test would then fail.
    //
    // POSIX-only: it relies on `sh` subshell + job-control semantics. On Windows
    // BashTool runs cmd.exe and this construction does not transfer. xunit 2.9
    // has no runtime Assert.Skip, so on Windows we early-return (a pass): the
    // tree-kill guarantee is verified on Linux/macOS, where BashTool spawns `sh`.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_Cancelled_KillsWholeTree_GrandchildStopsTicking()
    {
        if (OperatingSystem.IsWindows())
            return; // see note above: POSIX-only proof, BashTool uses cmd.exe here

        var marker = Path.Combine(Path.GetTempPath(), $"astra-killtree-{Guid.NewGuid():N}.log");
        // The shell ( = child of dotnet ) backgrounds a subshell ( = grandchild )
        // that appends a tick every 100ms, prints one line so the tool streams,
        // then blocks on `wait` so the process stays alive until it is killed.
        var command =
            $"( while true; do echo tick >> '{marker}'; sleep 0.1; done ) & echo started; wait";

        var bash = new BashTool();
        using var cts = new CancellationTokenSource();

        // Drain the tool on a background task; it blocks until we cancel.
        var drained = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in bash.ExecuteAsync(Cmd(command), cts.Token)) { }
            }
            catch (OperationCanceledException) { /* expected: cancelled tool produces no Result */ }
        });

        try
        {
            // Wait until the grandchild is demonstrably alive and writing.
            await WaitUntilAsync(
                () => File.Exists(marker) && CountLines(marker) >= 2,
                timeout: TimeSpan.FromSeconds(10));

            // Stop. The tool's finally must kill the tree (grandchild included) and reap.
            cts.Cancel();
            await drained;

            // Sample right after the kill settles, then again after many tick periods.
            // A live grandchild writes ~10 lines/sec; 800ms would add ~8 lines.
            await Task.Delay(200);
            var afterKill = CountLines(marker);
            await Task.Delay(800);
            var later = CountLines(marker);

            Assert.Equal(afterKill, later); // no new ticks => the grandchild is dead
        }
        finally
        {
            try { File.Delete(marker); } catch { /* best effort cleanup */ }
        }
    }

    // ------------------------------------------------------------------
    // Cancellation is responsive AND the normal path is unaffected: a long-running
    // command, cancelled, makes the tool throw OperationCanceledException promptly
    // (well under the command's own runtime) rather than blocking to completion.
    // Cross-platform — no grandchild, just proves the cancel path is wired and the
    // finally does not deadlock on reap. Uses a sleep that is long on both shells.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_Cancelled_ThrowsPromptly_NotAfterFullRuntime()
    {
        var bash = new BashTool();
        // ~30s if it ran to completion; the test must finish in a fraction of that.
        var command = OperatingSystem.IsWindows()
            ? "ping -n 30 127.0.0.1 >nul"
            : "sleep 30";

        using var cts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var run = Task.Run(async () =>
        {
            await foreach (var _ in bash.ExecuteAsync(Cmd(command), cts.Token)) { }
        });

        // Let it get going, then cancel.
        await Task.Delay(300);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"cancellation took {sw.Elapsed.TotalSeconds:F1}s; it should be prompt, not the full runtime");
    }
}
