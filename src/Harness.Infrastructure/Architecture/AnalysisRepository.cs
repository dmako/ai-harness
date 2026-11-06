using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
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

    // What the analysis learned of the disk, kept while it lasts (TASK-0095): whether a path exists, where each
    // path segment leads once its links are followed, and each parsed MSBuild file. Project evaluation asks the
    // same questions thousands of times (every Directory.Build.props search walks up the same folders), and on
    // Windows each disk check costs about 0.1 ms.
    private readonly ConcurrentDictionary<string, bool> _exists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _segments = new(StringComparer.Ordinal);
    private int _diskProbes;
    private int _xmlParses;

    /// <summary>Path segments checked on disk (a directory or link test each); each distinct segment once.</summary>
    public int DiskProbes => _diskProbes;

    /// <summary>MSBuild files parsed; each file once, whichever reader asks.</summary>
    public int XmlParses => _xmlParses;

    internal ConcurrentDictionary<string, XElement?> Xml { get; } = new(StringComparer.Ordinal);

    internal void CountXmlParse() => Interlocked.Increment(ref _xmlParses);
    internal Dictionary<string, Harness.Core.Architecture.EvaluatedProject> Evaluations { get; } =
        new(StringComparer.Ordinal);

    public static Task<AnalysisRepository> OpenAsync(string root, CancellationToken token) =>
        OpenAsync(root, detached: false, token);

    /// <summary>
    /// Lists the repository's files. A detached analysis outlives the request that opened it (harness ui shares
    /// it between requests), so its reads never stop on that request's token.
    /// </summary>
    public static async Task<AnalysisRepository> OpenAsync(string root, bool detached, CancellationToken token)
    {
        var repository = await GitRepository.DiscoverAsync(root, token).ConfigureAwait(false);
        var files = await repository.ListFilesAsync(token).ConfigureAwait(false);
        return new(repository.Root, files, detached ? CancellationToken.None : token);
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
            actual = _segments.GetOrAdd(Path.Combine(actual, segment), Follow);
            if (!Within(actual))
            {
                throw new InvalidDataException("analysis path link leaves the repository");
            }
        }
        return actual;
    }

    /// <summary>Where a path segment leads: itself, or its link's final target.</summary>
    private string Follow(string path)
    {
        Interlocked.Increment(ref _diskProbes);
        FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return entry.LinkTarget is null
            ? path
            : entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException("cannot resolve analysis path link");
    }

    internal bool Within(string full) =>
        full.Equals(Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || full.StartsWith(
            Root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
        );

    internal bool Exists(string relative) =>
        _exists.GetOrAdd(
            relative,
            path =>
            {
                try
                {
                    return Path.Exists(Resolve(path));
                }
                catch (Exception error)
                    when (error
                        is IOException
                            or InvalidDataException
                            or UnauthorizedAccessException
                            or ArgumentException
                    )
                {
                    return false;
                }
            }
        );

    internal string Relative(string absolute) => Path.GetRelativePath(Root, absolute).Replace('\\', '/');

    internal static string[] Lines(string text) => text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
}
