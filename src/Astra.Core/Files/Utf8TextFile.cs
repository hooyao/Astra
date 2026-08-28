using System.Text;

namespace Astra.Core.Files;

internal readonly record struct Utf8TextSnapshot(string Content, bool HasByteOrderMark);

internal readonly record struct Utf8TextLine(string Content, string Terminator)
{
    public int Length => Content.Length + Terminator.Length;
}

/// <summary>
/// Strict UTF-8 helpers shared by file tools. The line reader retains each
/// original line terminator so text copied from Read remains suitable for an
/// exact Edit operation.
/// </summary>
internal static class Utf8TextFile
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static async Task<Utf8TextSnapshot> ReadAllAsync(
        string path,
        CancellationToken ct)
    {
        var (stream, hasByteOrderMark) = OpenRead(path);
        await using (stream)
        using (var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: false))
        {
            var content = await reader.ReadToEndAsync(ct);
            return new Utf8TextSnapshot(content, hasByteOrderMark);
        }
    }

    public static Utf8TextLineReader OpenLineReader(string path)
    {
        var (stream, _) = OpenRead(path);
        return new Utf8TextLineReader(stream, StrictUtf8);
    }

    private static (FileStream Stream, bool HasByteOrderMark) OpenRead(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        try
        {
            Span<byte> prefix = stackalloc byte[4];
            var length = 0;
            while (length < prefix.Length)
            {
                var read = stream.Read(prefix[length..]);
                if (read == 0)
                    break;
                length += read;
            }
            var hasUtf8Bom = length >= 3 &&
                prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf;

            if (!hasUtf8Bom && IsUtf16OrUtf32Bom(prefix[..length]))
                throw new InvalidDataException("The file is not UTF-8 text.");

            stream.Position = hasUtf8Bom ? 3 : 0;
            return (stream, hasUtf8Bom);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool IsUtf16OrUtf32Bom(ReadOnlySpan<byte> prefix) =>
        prefix.Length >= 4 &&
        ((prefix[0] == 0xff && prefix[1] == 0xfe && prefix[2] == 0x00 && prefix[3] == 0x00) ||
         (prefix[0] == 0x00 && prefix[1] == 0x00 && prefix[2] == 0xfe && prefix[3] == 0xff)) ||
        prefix.Length >= 2 &&
        ((prefix[0] == 0xff && prefix[1] == 0xfe) ||
         (prefix[0] == 0xfe && prefix[1] == 0xff));
}

internal sealed class Utf8TextLineReader : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly char[] _buffer = new char[16 * 1024];
    private int _position;
    private int _length;
    private bool _completed;

    public Utf8TextLineReader(FileStream stream, Encoding encoding)
    {
        _reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: false);
    }

    public async ValueTask<Utf8TextLine?> ReadLineAsync(CancellationToken ct)
    {
        var content = new StringBuilder();

        while (true)
        {
            if (!await EnsureBufferedAsync(ct))
            {
                return content.Length == 0
                    ? null
                    : new Utf8TextLine(content.ToString(), string.Empty);
            }

            var remaining = _buffer.AsSpan(_position, _length - _position);
            var terminatorIndex = remaining.IndexOfAny('\r', '\n');
            if (terminatorIndex < 0)
            {
                content.Append(remaining);
                _position = _length;
                continue;
            }

            if (terminatorIndex > 0)
            {
                content.Append(remaining[..terminatorIndex]);
                _position += terminatorIndex;
            }

            var first = _buffer[_position++];
            if (first == '\n')
                return new Utf8TextLine(content.ToString(), "\n");

            if (await EnsureBufferedAsync(ct) && _buffer[_position] == '\n')
            {
                _position++;
                return new Utf8TextLine(content.ToString(), "\r\n");
            }

            return new Utf8TextLine(content.ToString(), "\r");
        }
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> EnsureBufferedAsync(CancellationToken ct)
    {
        if (_position < _length)
            return true;
        if (_completed)
            return false;

        _length = await _reader.ReadAsync(_buffer.AsMemory(), ct);
        _position = 0;
        _completed = _length == 0;
        return !_completed;
    }
}
