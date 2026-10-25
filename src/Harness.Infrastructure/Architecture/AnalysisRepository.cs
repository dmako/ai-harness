using System.Text;
using Harness.Infrastructure.Git;

namespace Harness.Infrastructure.Architecture;

/// <summary>One analysis request owns its file list, successful reads and failed reads.</summary>
public sealed class AnalysisRepository(string root, IReadOnlyList<string> files, CancellationToken token = default)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public IReadOnlyList<string> Files { get; } = files;
    public List<string> Notes { get; } = [];
    public int PhysicalReads { get; private set; }
    internal Dictionary<string, Harness.Core.Architecture.EvaluatedProject> Evaluations { get; } =
        new(StringComparer.Ordinal);

    public static async Task<AnalysisRepository> OpenAsync(string root, CancellationToken token)
    {
        var repository = await GitRepository.DiscoverAsync(root, token).ConfigureAwait(false);
        return new(repository.Root, await repository.ListFilesAsync(token).ConfigureAwait(false), token);
    }

    public string Read(string relative)
    {
        token.ThrowIfCancellationRequested();
        if (_text.TryGetValue(relative, out var cached))
        {
            return cached;
        }

        var text = "";
        try
        {
            var path = Resolve(relative);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            if (stream.Length > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("analysis file exceeds the 4 MiB read limit");
            }

            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (output.Length + count > 4 * 1024 * 1024)
                {
                    throw new InvalidDataException("analysis file exceeds the 4 MiB read limit");
                }

                output.Write(buffer.AsSpan(0, count));
            }
            PhysicalReads++;
            text = Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            Notes.Add(relative + ": " + error.Message.Split('\n')[0]);
        }
        _text.Add(relative, text);
        return text;
    }

    internal string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Length is 0)
        {
            throw new InvalidDataException("analysis path must be relative to the repository");
        }

        var full = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(full))
        {
            throw new InvalidDataException("analysis path leaves the repository");
        }

        var actual = Root;
        foreach (var segment in Path.GetRelativePath(Root, full).Split(Path.DirectorySeparatorChar))
        {
            actual = Path.Combine(actual, segment);
            FileSystemInfo entry = Directory.Exists(actual) ? new DirectoryInfo(actual) : new FileInfo(actual);
            if (entry.LinkTarget is not null)
            {
                actual =
                    entry.ResolveLinkTarget(true)?.FullName
                    ?? throw new IOException("cannot resolve analysis path link");
            }

            if (!Within(actual))
            {
                throw new InvalidDataException("analysis path link leaves the repository");
            }
        }
        return actual;
    }

    internal bool Within(string full) =>
        full.Equals(Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || full.StartsWith(
            Root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );

    internal bool Exists(string relative)
    {
        try
        {
            return Path.Exists(Resolve(relative));
        }
        catch (Exception error)
            when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal string Relative(string absolute) => Path.GetRelativePath(Root, absolute).Replace('\\', '/');

    internal static string[] Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
}
