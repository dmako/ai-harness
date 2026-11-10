using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Harness.Infrastructure.Git;

/// <summary>
/// Reads files as they were at a commit through one <c>git cat-file --batch</c> process, started on the first read
/// (TASK-0100): the same contents as <c>git show commit:path</c> without a git process per file.
/// </summary>
public sealed class GitBlobReader : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly string[] Arguments = ["cat-file", "--batch"];
    private static readonly UTF8Encoding Strict = new(false, true);

    private readonly string _root;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private Process? _process;
    private Stream? _output;
    private Task<string>? _errors;
    private int _start;
    private int _end;

    internal GitBlobReader(string root) => _root = root;

    /// <summary>
    /// The file at <paramref name="path"/> in <paramref name="commit"/> as UTF-8 text; null when the commit has no
    /// such file, the path names something else than a file, or the file is not UTF-8 text. A git failure or a
    /// path git cannot be asked for in a line throws <see cref="IOException"/>.
    /// </summary>
    public async Task<string?> ReadAsync(string commit, string path, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(path);
        if (path.AsSpan().IndexOfAny('\n', '\r') >= 0 || commit.AsSpan().IndexOfAny('\n', '\r') >= 0)
        {
            throw new IOException("A line break cannot be read through git cat-file --batch.");
        }

        if (string.IsNullOrEmpty(commit) || commit[0] is '-' || commit.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid Git revision.");
        }

        var process = Start();
        try
        {
            return await ReadObjectAsync(process, commit + ":" + path, token)
                .WaitAsync(Deadline, token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException)
        {
            // a reply that did not arrive leaves the stream in an unknown place: no further reads from it
            Stop();
            throw error is IOException ? error : new IOException("git cat-file --batch did not answer.", error);
        }
    }

    private Process Start()
    {
        if (_process is { } running)
        {
            return running.HasExited ? throw new IOException("git cat-file --batch has stopped.") : running;
        }

        var process = new Process { StartInfo = GitRepository.GitStart(_root, Arguments) };
        GitRepository.StartGit(process, _root, Arguments);
        _process = process;
        _output = process.StandardOutput.BaseStream;
        _errors = process.StandardError.ReadToEndAsync();
        return process;
    }

    private async Task<string?> ReadObjectAsync(Process process, string name, CancellationToken token)
    {
        await process.StandardInput.WriteAsync((name + "\n").AsMemory(), token).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        // "<object> <type> <size>" then the contents and a line feed, or "<name> missing" / "<name> ambiguous"
        var header = Encoding.UTF8.GetString(await ReadLineAsync(token).ConfigureAwait(false));
        var fields = header.Split(' ');
        if (
            fields.Length is not 3
            || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || size > int.MaxValue - 1
        )
        {
            return
                header.EndsWith(" missing", StringComparison.Ordinal)
                || header.EndsWith(" ambiguous", StringComparison.Ordinal)
                ? null
                : throw new IOException("git cat-file --batch: unexpected reply " + header);
        }

        var contents = await ReadBytesAsync((int)size + 1, token).ConfigureAwait(false);
        if (fields[1] is not "blob")
        {
            return null;
        }

        try
        {
            return Strict.GetString(contents, 0, contents.Length - 1);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private async Task<byte[]> ReadLineAsync(CancellationToken token)
    {
        List<byte> line = [];
        while (true)
        {
            if (_start == _end && !await FillAsync(token).ConfigureAwait(false))
            {
                throw new IOException("git cat-file --batch ended its output.");
            }

            var feed = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (feed >= 0)
            {
                line.AddRange(new ArraySegment<byte>(_buffer, _start, feed - _start));
                _start = feed + 1;
                return [.. line];
            }

            line.AddRange(new ArraySegment<byte>(_buffer, _start, _end - _start));
            _start = _end;
        }
    }

    private async Task<byte[]> ReadBytesAsync(int count, CancellationToken token)
    {
        var result = new byte[count];
        var filled = 0;
        while (filled < count)
        {
            if (_start == _end && !await FillAsync(token).ConfigureAwait(false))
            {
                throw new IOException("git cat-file --batch ended its output.");
            }

            var take = Math.Min(count - filled, _end - _start);
            Array.Copy(_buffer, _start, result, filled, take);
            _start += take;
            filled += take;
        }

        return result;
    }

    private async Task<bool> FillAsync(CancellationToken token)
    {
        _start = 0;
        _end = await _output!.ReadAsync(_buffer, token).ConfigureAwait(false);
        return _end > 0;
    }

    private void Stop()
    {
        if (_process is { HasExited: false } process)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close(); // end of input: git cat-file --batch exits
                using var deadline = new CancellationTokenSource(Deadline);
                try
                {
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (_errors is { } errors)
            {
                _ = await errors.ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or DecoderFallbackException)
        { /* Disposing a reader that failed: nothing more to read. */
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }
}
