using Harness.Core.Retrieval;

namespace Harness.Infrastructure.Retrieval;

/// <summary>Request-scoped file bodies; provider-supplied paths may not leave the repository.</summary>
public sealed class RetrievalFileReader
{
    private readonly string _root;
    private readonly FileReadCache _cache;

    public RetrievalFileReader(string root)
    {
        _root = Path.GetFullPath(root);
        _cache = new(LoadAsync);
    }

    public Task<string> ReadBodyAsync(string path, int start, int end, CancellationToken cancellationToken = default) =>
        _cache.ReadBodyAsync(path, start, end, cancellationToken);

    private async Task<byte[]> LoadAsync(string relative, CancellationToken token)
    {
        var root = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (Path.IsPathRooted(relative) || relative.Length is 0 || relative.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Body path must be relative to the repository.");
        }

        var path = Path.GetFullPath(Path.Combine(_root, relative));
        if (
            !path.StartsWith(
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException("Body path leaves the repository.");
        }

        var actual = _root;
        foreach (var segment in Path.GetRelativePath(_root, path).Split(Path.DirectorySeparatorChar))
        {
            actual = Path.Combine(actual, segment);
            FileSystemInfo entry = Directory.Exists(actual) ? new DirectoryInfo(actual) : new FileInfo(actual);
            if (entry.LinkTarget is not null)
            {
                actual =
                    entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Cannot resolve body path link.");
            }

            if (
                !Path.GetFullPath(actual)
                    .StartsWith(
                        root,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                    )
            )
            {
                throw new InvalidDataException("Body path link leaves the repository.");
            }
        }
        await using var file = new FileStream(
            actual,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        const int Maximum = 4 * 1024 * 1024;
        if (file.Length > Maximum)
        {
            throw new InvalidDataException("Body file exceeds the 4 MiB read limit.");
        }

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await file.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > Maximum)
            {
                throw new InvalidDataException("Body file exceeds the 4 MiB read limit.");
            }

            output.Write(buffer.AsSpan(0, count));
        }
        return output.ToArray();
    }
}
