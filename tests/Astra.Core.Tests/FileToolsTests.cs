using Astra.Core.Files;
using System.Text;
using Xunit;

namespace Astra.Core.Tests;

public sealed class FileToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"AstraFileToolsTests-{Guid.NewGuid():N}");
    private readonly WorkspaceFileSystem _fileSystem;

    public FileToolsTests()
    {
        Directory.CreateDirectory(_root);
        _fileSystem = new WorkspaceFileSystem(_root, [_root]);
    }

    [Fact]
    public async Task DefaultMode_AllowsAbsolutePathOutsideWorkingDirectory()
    {
        var otherRoot = Path.Combine(
            Path.GetTempPath(),
            $"AstraFileToolsUnrestricted-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherRoot);
        var target = Path.Combine(otherRoot, "outside.txt");

        try
        {
            var fileSystem = new WorkspaceFileSystem(_root);
            var write = new WriteFileTool(fileSystem);

            await ExecuteAsync(write, new Dictionary<string, object?>
            {
                ["file_path"] = target,
                ["content"] = "allowed",
            });

            Assert.False(fileSystem.IsRestricted);
            Assert.Equal("allowed", await File.ReadAllTextAsync(target));
        }
        finally
        {
            TryDeleteDirectory(otherRoot);
        }
    }

    [Fact]
    public async Task RestrictedMode_AllowsMultipleRoots_AndRejectsAThird()
    {
        var secondRoot = Path.Combine(
            Path.GetTempPath(),
            $"AstraFileToolsSecond-{Guid.NewGuid():N}");
        var thirdRoot = Path.Combine(
            Path.GetTempPath(),
            $"AstraFileToolsThird-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        Directory.CreateDirectory(thirdRoot);

        try
        {
            var fileSystem = new WorkspaceFileSystem(_root, [_root, secondRoot]);
            var write = new WriteFileTool(fileSystem);
            var allowed = Path.Combine(secondRoot, "allowed.txt");
            var denied = Path.Combine(thirdRoot, "denied.txt");

            await ExecuteAsync(write, new Dictionary<string, object?>
            {
                ["file_path"] = allowed,
                ["content"] = "allowed",
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ExecuteAsync(write, new Dictionary<string, object?>
                {
                    ["file_path"] = denied,
                    ["content"] = "denied",
                }));

            Assert.True(fileSystem.IsRestricted);
            Assert.Equal(2, fileSystem.AllowedRoots.Count);
            Assert.True(File.Exists(allowed));
            Assert.False(File.Exists(denied));
        }
        finally
        {
            TryDeleteDirectory(secondRoot);
            TryDeleteDirectory(thirdRoot);
        }
    }

    [Fact]
    public async Task ReadFile_ReturnsRequestedLineRange_AndContinuationOffset()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, "sample.txt"),
            "line-1\nline-2\nline-3\nline-4");
        var tool = new ReadFileTool(_fileSystem);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = "sample.txt",
            ["offset"] = 2,
            ["limit"] = 2,
        });

        Assert.Equal(ToolAction.Read, tool.Classify(null));
        Assert.Contains("line-2", result);
        Assert.Contains("line-3", result);
        Assert.DoesNotContain("line-1", result);
        Assert.DoesNotContain("line-4", result);
        Assert.Contains("offset=4", result);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ReadFile_PreservesOriginalLineTerminators(string newline)
    {
        var path = Path.Combine(_root, "newlines.txt");
        var original = $"line-1{newline}line-2{newline}line-3{newline}line-4";
        await File.WriteAllTextAsync(path, original, new UTF8Encoding(false));
        var tool = new ReadFileTool(_fileSystem);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = "newlines.txt",
            ["offset"] = 2,
            ["limit"] = 2,
        });

        Assert.Equal($"line-2{newline}line-3{newline}", ExtractReadPayload(result));
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectories_AndOverwritesCompleteContent()
    {
        var tool = new WriteFileTool(_fileSystem);
        var path = Path.Combine(_root, "nested", "created.txt");

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = Path.Combine("nested", "created.txt"),
            ["content"] = string.Empty,
        });

        Assert.Equal(ToolAction.Write, tool.Classify(null));
        Assert.True(File.Exists(path));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = Path.Combine("nested", "created.txt"),
            ["content"] = "replacement",
        });
        Assert.Equal("replacement", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task EditFile_RequiresUniqueMatch_UnlessReplaceAllIsExplicit()
    {
        var path = Path.Combine(_root, "edit.txt");
        await File.WriteAllTextAsync(path, "alpha beta beta");
        var tool = new EditFileTool(_fileSystem);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = "edit.txt",
            ["old_string"] = "alpha ",
            ["new_string"] = string.Empty,
        });
        Assert.Equal("beta beta", await File.ReadAllTextAsync(path));

        var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecuteAsync(tool, new Dictionary<string, object?>
            {
                ["file_path"] = "edit.txt",
                ["old_string"] = "beta",
                ["new_string"] = "gamma",
            }));
        Assert.Contains("2 times", ambiguous.Message);
        Assert.Equal("beta beta", await File.ReadAllTextAsync(path));

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = "edit.txt",
            ["old_string"] = "beta",
            ["new_string"] = "gamma",
            ["replace_all"] = true,
        });
        Assert.Equal("gamma gamma", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public async Task EditFile_PreservesLineTerminators_AndUtf8Bom(
        string newline,
        bool withBom)
    {
        var path = Path.Combine(_root, "preserve-format.txt");
        var original = $"before{newline}hello world{newline}after{newline}";
        await WriteUtf8Async(path, original, withBom);
        var tool = new EditFileTool(_fileSystem);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = "preserve-format.txt",
            ["old_string"] = $"before{newline}hello world{newline}after",
            ["new_string"] = $"before{newline}hello astra{newline}after",
        });

        var actual = await File.ReadAllBytesAsync(path);
        var expectedText = $"before{newline}hello astra{newline}after{newline}";
        Assert.Equal(Utf8Bytes(expectedText, withBom), actual);
    }

    [Fact]
    public async Task EditFile_AllowsWhitespaceOnlyExactMatch()
    {
        var path = Path.Combine(_root, "whitespace.txt");
        await File.WriteAllTextAsync(path, "left    right");
        var tool = new EditFileTool(_fileSystem);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["file_path"] = "whitespace.txt",
            ["old_string"] = "    ",
            ["new_string"] = "\t",
        });

        Assert.Equal("left\tright", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Grep_FindsLiteralInLargeFile_WithoutAnOffset()
    {
        const int targetLine = 23_456;
        var lines = Enumerable.Range(1, 30_000)
            .Select(line => line == targetLine
                ? "prefix hello world suffix"
                : $"ordinary line {line}")
            .ToArray();
        await File.WriteAllTextAsync(
            Path.Combine(_root, "large.txt"),
            string.Join('\n', lines));
        var tool = new GrepTool(_fileSystem);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "large.txt",
            ["pattern"] = "hello world",
            ["output_mode"] = "content",
        });

        Assert.Equal(ToolAction.Read, tool.Classify(null));
        Assert.Contains("Entries returned: 1.", result);
        Assert.Contains("large.txt:23456:8: prefix hello world suffix", result);
    }

    [Fact]
    public async Task Grep_Recurses_FiltersFiles_AndSupportsRegexCaseFolding()
    {
        var subdirectory = Path.Combine(_root, "subdirectory");
        Directory.CreateDirectory(subdirectory);
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "target.cs"), "value = HELLO   ASTRA;");
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "ignored.txt"), "hello astra");
        var tool = new GrepTool(_fileSystem);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = ".",
            ["pattern"] = @"hello\s+astra",
            ["-i"] = true,
            ["glob"] = "*.cs",
            ["output_mode"] = "content",
        });

        Assert.Contains($"subdirectory{Path.DirectorySeparatorChar}target.cs:1:9:", result);
        Assert.DoesNotContain("ignored.txt", result);
    }

    [Fact]
    public async Task Grep_BoundsResults_AndReportsTheLimit()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "many.txt"), "match\nmatch\nmatch");
        var tool = new GrepTool(_fileSystem);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "many.txt",
            ["pattern"] = "match",
            ["output_mode"] = "content",
            ["head_limit"] = 2,
        });

        Assert.Contains("Entries returned: 2. Results limited to head_limit=2 after offset=0.", result);
    }

    [Fact]
    public async Task Grep_SupportsDefaultFileMode_CountMode_AndPagination()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "first.cs"), "needle needle");
        await File.WriteAllTextAsync(Path.Combine(_root, "second.cs"), "needle");
        var tool = new GrepTool(_fileSystem);

        var files = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["glob"] = "*.cs",
        });
        Assert.Contains("Mode: files_with_matches", files);
        Assert.Contains("first.cs", files);
        Assert.Contains("second.cs", files);

        var counts = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["glob"] = "*.cs",
            ["output_mode"] = "count",
            ["head_limit"] = 1,
        });
        Assert.Contains("Entries returned: 1.", counts);
        Assert.NotEqual(
            counts.Contains("first.cs:2", StringComparison.Ordinal),
            counts.Contains("second.cs:1", StringComparison.Ordinal));

        await File.WriteAllTextAsync(Path.Combine(_root, "page.txt"), "needle one\nneedle two\nneedle three");
        var page = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = "page.txt",
            ["pattern"] = "needle",
            ["output_mode"] = "content",
            ["offset"] = 1,
            ["head_limit"] = 1,
        });
        Assert.Contains("page.txt:2:1: needle two", page);
        Assert.DoesNotContain("page.txt:1:1:", page);
    }

    [Fact]
    public async Task Glob_SupportsRecursivePatterns_AndBraceAlternatives()
    {
        var subdirectory = Path.Combine(_root, "src", "nested");
        Directory.CreateDirectory(subdirectory);
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "one.cs"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "two.csproj"), string.Empty);
        await File.WriteAllTextAsync(Path.Combine(subdirectory, "ignored.txt"), string.Empty);
        var tool = new GlobTool(_fileSystem);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.{cs,csproj}",
        });

        Assert.Equal(ToolAction.Read, tool.Classify(null));
        Assert.Contains($"src{Path.DirectorySeparatorChar}nested{Path.DirectorySeparatorChar}one.cs", result);
        Assert.Contains($"src{Path.DirectorySeparatorChar}nested{Path.DirectorySeparatorChar}two.csproj", result);
        Assert.DoesNotContain("ignored.txt", result);
    }

    [Fact]
    public async Task GlobAndGrep_SkipVersionControlMetadata()
    {
        var metadata = Path.Combine(_root, ".git");
        Directory.CreateDirectory(metadata);
        await File.WriteAllTextAsync(Path.Combine(metadata, "hidden.cs"), "SECRET_MARKER");
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.cs"), "public marker");

        var glob = await ExecuteAsync(new GlobTool(_fileSystem), new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs",
        });
        var grep = await ExecuteAsync(new GrepTool(_fileSystem), new Dictionary<string, object?>
        {
            ["pattern"] = "SECRET_MARKER",
            ["output_mode"] = "content",
        });

        Assert.Contains("visible.cs", glob);
        Assert.DoesNotContain("hidden.cs", glob);
        Assert.Contains("No matches found.", grep);
    }

    [Fact]
    public async Task FileTools_RejectLexicalWorkspaceEscape()
    {
        var read = new ReadFileTool(_fileSystem);
        var write = new WriteFileTool(_fileSystem);
        var outside = Path.Combine(_root, "..", $"outside-{Guid.NewGuid():N}.txt");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ExecuteAsync(read, new Dictionary<string, object?> { ["file_path"] = outside }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            ExecuteAsync(write, new Dictionary<string, object?>
            {
                ["file_path"] = outside,
                ["content"] = "blocked",
            }));
        Assert.False(File.Exists(Path.GetFullPath(outside)));
    }

    [Fact]
    public async Task FileTools_RejectSymlinkEscape_WhenPlatformPermitsSymlinks()
    {
        var outsideDirectory = Path.Combine(
            Path.GetTempPath(),
            $"AstraFileToolsOutside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDirectory);
        var outsideFile = Path.Combine(outsideDirectory, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "secret");
        var link = Path.Combine(_root, "outside-link");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, outsideDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var read = new ReadFileTool(_fileSystem);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ExecuteAsync(read, new Dictionary<string, object?>
                {
                    ["file_path"] = Path.Combine("outside-link", "secret.txt"),
                }));
        }
        finally
        {
            try
            {
                if (Directory.Exists(link))
                    Directory.Delete(link);
                Directory.Delete(outsideDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<string> ExecuteAsync(
        ITool tool,
        IDictionary<string, object?> arguments)
    {
        var outputs = new List<ToolOutput>();
        await foreach (var output in tool.ExecuteAsync(arguments, CancellationToken.None))
            outputs.Add(output);

        return Assert.IsType<ToolOutput.Result>(Assert.Single(outputs)).Text;
    }

    private static string ExtractReadPayload(string result)
    {
        var headerEnd = result.IndexOf("\n\n", StringComparison.Ordinal);
        Assert.True(headerEnd >= 0);
        var payload = result[(headerEnd + 2)..];
        var suffixStart = payload.IndexOf("\n\n[", StringComparison.Ordinal);
        return suffixStart < 0 ? payload : payload[..suffixStart];
    }

    private static async Task WriteUtf8Async(string path, string content, bool withBom) =>
        await File.WriteAllBytesAsync(path, Utf8Bytes(content, withBom));

    private static byte[] Utf8Bytes(string content, bool withBom)
    {
        var encoding = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: withBom,
            throwOnInvalidBytes: true);
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(content)];
    }

    public void Dispose()
    {
        TryDeleteDirectory(_root);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
