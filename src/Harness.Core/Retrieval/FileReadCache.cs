using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Harness.Core.Retrieval;

/// <summary>One instance per request: concurrent reads share successes and failures, never a global stale file.</summary>
public sealed class FileReadCache(
    Func<string, CancellationToken, Task<byte[]>> load,
    long maximumBytes = 32 * 1024 * 1024
)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ConcurrentDictionary<string, Lazy<Task<string[]>>> _files = new(StringComparer.Ordinal);
    private long _loadedBytes;

    public async Task<string> ReadBodyAsync(
        string path,
        int start,
        int end,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var read = _files
            .GetOrAdd(
                path,
                key => new(() => LoadAsync(key, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication)
            )
            .Value;
        _ = read.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
        var lines = await read.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (start < 1 || start > lines.Length)
        {
            throw new IOException(
                path + ": line " + start.ToString(CultureInfo.InvariantCulture) + " outside the file"
            );
        }

        end = Math.Min(Math.Max(end, start), lines.Length);
        return string.Join('\n', lines[(start - 1)..end]);
    }

    private async Task<string[]> LoadAsync(string path, CancellationToken token)
    {
        var bytes = await load(path, token).ConfigureAwait(false);
        if (Interlocked.Add(ref _loadedBytes, bytes.LongLength) > maximumBytes)
        {
            throw new InvalidDataException("Request file-read cache byte limit exceeded.");
        }

        return Utf8.GetString(bytes).Split('\n');
    }
}
