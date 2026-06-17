using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;
using Xunit;

namespace Astra.Core.Tests;

/// <summary>
/// D2 — the tool permission contract. These tests prove the thesis: a single
/// tool type returns different <see cref="ToolAction"/> categories depending on
/// its input (behavioral flags over inheritance), and the fail-closed default
/// interface method routes the unclassified case to the strictest bucket.
/// </summary>
public class BashToolTests
{
    private static IDictionary<string, object?> Cmd(string command) =>
        new Dictionary<string, object?> { ["command"] = command };

    // ------------------------------------------------------------------
    // The load-bearing demo: ONE tool, category varies by argument.
    // "ls" is a Read; "rm -rf" is an Execute. If this were inheritance
    // (ReadOnlyTool / WriteTool) it could not be expressed on one type.
    // ------------------------------------------------------------------
    [Fact]
    public void Classify_IsInputDependent_LsIsReadRmIsExecute()
    {
        var bash = new BashTool();

        Assert.Equal(ToolAction.Read, bash.Classify(Cmd("ls -la")));
        Assert.Equal(ToolAction.Execute, bash.Classify(Cmd("rm -rf /tmp/x")));
    }

    [Theory]
    [InlineData("ls")]
    [InlineData("ls -la /var")]
    [InlineData("cat file.txt")]
    [InlineData("pwd")]
    [InlineData("grep -n foo bar.txt")]
    [InlineData("head -5 log")]
    [InlineData("tail -f log")]
    [InlineData("find . -name *.cs")]
    public void Classify_ReadCommands_AreRead(string command) =>
        Assert.Equal(ToolAction.Read, new BashTool().Classify(Cmd(command)));

    [Theory]
    [InlineData("rm file")]
    [InlineData("rm -rf /")]
    [InlineData("mv a b")]
    [InlineData("dd if=/dev/zero of=/dev/sda")]
    [InlineData("mkfs.ext4 /dev/sdb")]
    public void Classify_ExecuteCommands_AreExecute(string command) =>
        Assert.Equal(ToolAction.Execute, new BashTool().Classify(Cmd(command)));

    [Theory]
    [InlineData("touch newfile")]
    [InlineData("mkdir dir")]
    [InlineData("tee out.txt")]
    public void Classify_WriteCommands_AreWrite(string command) =>
        Assert.Equal(ToolAction.Write, new BashTool().Classify(Cmd(command)));

    // ------------------------------------------------------------------
    // Redirection makes an otherwise-read command a Write, and it must win
    // over the Read classification of the verb ("echo" alone is a Read).
    // ------------------------------------------------------------------
    [Fact]
    public void Classify_OutputRedirection_IsWrite_EvenForReadVerb()
    {
        var bash = new BashTool();

        Assert.Equal(ToolAction.Read, bash.Classify(Cmd("echo hi")));
        Assert.Equal(ToolAction.Write, bash.Classify(Cmd("echo hi > file.txt")));
    }

    // ------------------------------------------------------------------
    // Env-var assignment prefixes must not fool the classifier: it should
    // classify on the real command word, not "FOO=bar".
    // ------------------------------------------------------------------
    [Fact]
    public void Classify_SkipsLeadingEnvAssignments()
    {
        var bash = new BashTool();

        Assert.Equal(ToolAction.Execute, bash.Classify(Cmd("FOO=bar rm x")));
        Assert.Equal(ToolAction.Read, bash.Classify(Cmd("LANG=C ls")));
    }

    // ------------------------------------------------------------------
    // Fail-closed: an unrecognized command, and a missing/empty command,
    // both land in Other (the strictest bucket), never in a safe one.
    // ------------------------------------------------------------------
    [Theory]
    [InlineData("curl http://evil.com")]
    [InlineData("some_unknown_binary --flag")]
    public void Classify_UnknownCommand_IsOther(string command) =>
        Assert.Equal(ToolAction.Other, new BashTool().Classify(Cmd(command)));

    [Fact]
    public void Classify_NoCommandArgument_IsOther()
    {
        var bash = new BashTool();

        Assert.Equal(ToolAction.Other, bash.Classify(null));
        Assert.Equal(ToolAction.Other, bash.Classify(new Dictionary<string, object?>()));
        Assert.Equal(ToolAction.Other, bash.Classify(Cmd("   ")));
    }

    // ------------------------------------------------------------------
    // The bag may carry a JsonElement (wire format) rather than a string.
    // Classify must handle both — it does in production via the loop.
    // ------------------------------------------------------------------
    [Fact]
    public void Classify_AcceptsJsonElementCommand()
    {
        var json = JsonDocument.Parse("{\"command\":\"rm -rf x\"}").RootElement;
        var args = new Dictionary<string, object?> { ["command"] = json.GetProperty("command") };

        Assert.Equal(ToolAction.Execute, new BashTool().Classify(args));
    }

    // ------------------------------------------------------------------
    // Streaming contract (B): a multi-line command yields each line as a live
    // Progress item, then exactly one Result as the last item. The Result is the
    // accumulated output. Proves output is streamed, not delivered in one blob.
    // ------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_StreamsProgressThenSingleResult()
    {
        var bash = new BashTool();
        // Three lines on both shells: cmd.exe `echo a & echo b & echo c`, sh `printf`.
        var command = OperatingSystem.IsWindows()
            ? "echo a& echo b& echo c"
            : "printf 'a\\nb\\nc\\n'";

        var outputs = new List<ToolOutput>();
        await foreach (var o in bash.ExecuteAsync(Cmd(command)))
            outputs.Add(o);

        // Exactly one Result, and it is the LAST item.
        Assert.IsType<ToolOutput.Result>(outputs[^1]);
        Assert.Single(outputs.OfType<ToolOutput.Result>());

        // At least one Progress item arrived before the Result (streaming, not blob).
        var progress = outputs.OfType<ToolOutput.Progress>().ToList();
        Assert.NotEmpty(progress);

        // The three lines showed up as progress, and the Result accumulates them.
        var result = ((ToolOutput.Result)outputs[^1]).Text;
        foreach (var line in new[] { "a", "b", "c" })
        {
            Assert.Contains(progress, p => p.Text.Trim() == line);
            Assert.Contains(line, result);
        }
    }
}

/// <summary>
/// D2 — the fail-closed default interface method. A tool that does NOT override
/// Classify must be treated as <see cref="ToolAction.Other"/>, and — the .NET
/// caveat worth pinning down in a test — that default is only reachable through
/// an <see cref="ITool"/> reference.
/// </summary>
public class DefaultClassifyTests
{
    /// <summary>A minimal tool that declares no Classify — it relies on the DIM default.</summary>
    private sealed class NoClassifyTool : ITool
    {
        public string Name => "noop";
        public string Description => "does nothing";
        public JsonElement InputSchema { get; } =
            JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        public async IAsyncEnumerable<ToolOutput> ExecuteAsync(
            IDictionary<string, object?>? arguments,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new ToolOutput.Result("ok");
        }
    }

    [Fact]
    public void DefaultClassify_IsOther_FailClosed()
    {
        // Must go through the ITool reference: the default interface method is not
        // visible on the concrete type (NoClassifyTool declares no Classify).
        ITool tool = new NoClassifyTool();

        Assert.Equal(ToolAction.Other, tool.Classify(new Dictionary<string, object?> { ["x"] = "y" }));
        Assert.Equal(ToolAction.Other, tool.Classify(null));
    }
}
