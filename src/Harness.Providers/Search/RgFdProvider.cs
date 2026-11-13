using System.Globalization;
using System.Text;
using Harness.Core.Providers;
using Harness.Providers.Mcp;

namespace Harness.Providers.Search;

/// <summary>
/// The built-in text and file search (provider type builtin, registered as 'search'): code.text_search through
/// 'rg --json' and code.file_search through fd, or 'rg --files' filtered by the query without fd. Each call starts the
/// tool in the project root through the process factory, reads what it needs and stops its whole process tree, on
/// cancellation and at the provider's timeout too. Items have the rg-fd pack's fields: the path relative to the root,
/// with the line and its text for a match.
/// </summary>
public sealed class RgFdProvider : IProvider
{
    /// <summary>At most this much of a tool's output is read for one call; the answer is then truncated.</summary>
    internal const int OutputBytes = 2 * 1024 * 1024;

    private const int ErrorBytes = 4096;

    private readonly SearchToolset _tools;
    private readonly string _root;
    private readonly IProviderProcessFactory _factory;
    private readonly TimeSpan _timeout;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    public RgFdProvider(
        string name,
        SearchToolset tools,
        string root,
        IProviderProcessFactory factory,
        TimeSpan timeout = default,
        IReadOnlyDictionary<string, string?>? environment = null
    )
    {
        ArgumentNullException.ThrowIfNull(tools);
        Name = name;
        _tools = tools;
        _root = Path.GetFullPath(root);
        _factory = factory;
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(30);
        _environment = environment ?? new Dictionary<string, string?>();
    }

    public string Name { get; }

    public IReadOnlyList<CapabilityInfo> Capabilities() =>
        new[]
        {
            (Capability: Core.Providers.Capabilities.FileSearch, Offered: _tools.Any),
            (Capability: Core.Providers.Capabilities.TextSearch, Offered: _tools.HasRg),
        }
            .Where(entry => entry.Offered)
            .Select(entry => new CapabilityInfo(entry.Capability, Name, "medium", "medium", "live", "project"))
            .ToArray();

    public ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default)
    {
        var detail = (_tools.HasRg, _tools.HasFd) switch
        {
            (true, true) => $"built-in: rg {_tools.Rg}, fd {_tools.Fd}",
            (true, false) => $"built-in: rg {_tools.Rg}; no fd: file search through 'rg --files'",
            _ => $"built-in: fd {_tools.Fd}; no rg: no text search",
        };
        var missing = new[] { _tools.Rg, _tools.Fd }
            .Where(tool => probe && tool.Length > 0 && Path.IsPathFullyQualified(tool) && !File.Exists(tool))
            .ToArray();
        if (missing.Length > 0)
        {
            return ValueTask.FromResult(
                new ProviderHealth(
                    "unavailable",
                    "not found any more: " + string.Join(", ", missing),
                    SearchTools.InstallHint()
                )
            );
        }

        var fix = _tools.HasRg && _tools.HasFd ? null : SearchTools.InstallHint();
        return ValueTask.FromResult(new ProviderHealth("ready", detail, fix));
    }

    public async Task<ProviderResult> ExecuteAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = request.Limit > 0 ? request.Limit : 20;
        var scope = Scope(request.Path);
        return request.Capability switch
        {
            Core.Providers.Capabilities.TextSearch when _tools.HasRg => await TextAsync(
                    request,
                    scope,
                    limit,
                    cancellationToken
                )
                .ConfigureAwait(false),
            Core.Providers.Capabilities.FileSearch when _tools.HasFd => await FilesAsync(
                    request,
                    "fd",
                    _tools.Fd,
                    SearchOutput.FdArguments(request.Query, scope),
                    null,
                    limit,
                    cancellationToken
                )
                .ConfigureAwait(false),
            Core.Providers.Capabilities.FileSearch when _tools.HasRg => await FilesAsync(
                    request,
                    "rg --files",
                    _tools.Rg,
                    SearchOutput.RgFilesArguments(scope),
                    SearchOutput.FileFilter(request.Query),
                    limit,
                    cancellationToken
                )
                .ConfigureAwait(false),
            _ => throw new UnsupportedCapabilityException(),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Matches in rg's path order; reading stops at one more than the limit (the answer is then truncated) and the
    /// tool is stopped.
    /// </summary>
    private async Task<ProviderResult> TextAsync(
        ProviderRequest request,
        string scope,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var items = new List<ProviderItem>();
        var truncated = false;
        var run = await RunAsync(
                _tools.Rg,
                SearchOutput.TextArguments(request.Query, scope),
                line =>
                {
                    if (SearchOutput.ParseTextLine(line) is not { } match)
                    {
                        return true;
                    }

                    var path = Relative(match.Path);
                    if (!request.Covers(path))
                    {
                        return true;
                    }

                    if (items.Count == limit)
                    {
                        truncated = true;
                        return false;
                    }

                    items.Add(
                        new()
                        {
                            Kind = "match",
                            Path = path,
                            StartLine = match.Line,
                            Snippet = match.Text,
                            Provider = Name,
                            Tool = "rg",
                        }
                    );
                    return true;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return Result(run, "rg", items, truncated);
    }

    /// <summary>
    /// The files fd or rg --files list, sorted by path; the filter (for rg --files) keeps those matching the query.
    /// </summary>
    private async Task<ProviderResult> FilesAsync(
        ProviderRequest request,
        string tool,
        string command,
        string[] arguments,
        Func<string, bool>? filter,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var run = await RunAsync(
                command,
                arguments,
                line =>
                {
                    if (SearchOutput.ParseFileLine(line) is not { } file || filter?.Invoke(file) is false)
                    {
                        return true;
                    }

                    var path = Relative(file);
                    if (request.Covers(path))
                    {
                        paths.Add(path);
                    }

                    // rg --files prints in path order: past one more than the limit the rest cannot come first.
                    return filter is null || paths.Count <= limit;
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        var items = paths
            .Take(limit)
            .Select(path => new ProviderItem
            {
                Kind = "file",
                Name = Path.GetFileName(path),
                Path = path,
                Provider = Name,
                Tool = tool,
            })
            .ToArray();
        return Result(run, tool, items, paths.Count > limit);
    }

    private ProviderResult Result(Run run, string tool, IReadOnlyList<ProviderItem> items, bool truncated)
    {
        if (run.TimedOut)
        {
            return new()
            {
                Bytes = run.Bytes,
                TimedOut = true,
                Error = new ProviderException(
                    Name,
                    string.Create(CultureInfo.InvariantCulture, $"timed out after {_timeout.TotalSeconds:g}s"),
                    tool
                ),
            };
        }

        // rg exits 1 when nothing matched, and 2 after an error even when it found matches (a file it could not read).
        if (!run.Stopped && run.ExitCode > 1 && items.Count is 0)
        {
            var message = McpMapping.FirstLine(run.Error);
            return new()
            {
                Bytes = run.Bytes,
                Error = new ProviderException(
                    Name,
                    message.Length > 0
                        ? message
                        : string.Create(CultureInfo.InvariantCulture, $"exited with code {run.ExitCode}"),
                    tool
                ),
            };
        }

        return new()
        {
            Items = items,
            Bytes = run.Bytes,
            Truncated = truncated || run.Capped,
        };
    }

    /// <summary>
    /// The request's path as the tools' search path: absolute ({abs_path}), and inside the project root; empty for the
    /// whole project.
    /// </summary>
    private string Scope(string path)
    {
        if (path.Length is 0)
        {
            return "";
        }

        var absolute = Path.GetFullPath(path, _root);
        var relative = Path.GetRelativePath(_root, absolute);
        if (
            Path.IsPathRooted(relative)
            || relative is ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("Provider request path must stay inside the project root.", nameof(path));
        }

        return absolute;
    }

    /// <summary>
    /// A path the tool printed, relative to the root with '/' as the rg-fd pack's items have it. rg --json prints
    /// paths as the system spells them (.\src\a.cs on Windows), whatever --path-separator says.
    /// </summary>
    private string Relative(string path) =>
        McpMapping.Relative(OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path, _root);

    private sealed record Run(int ExitCode, int Bytes, bool Stopped, bool Capped, bool TimedOut, string Error);

    /// <summary>
    /// Starts the tool in the project root and hands each output line to <paramref name="line"/> until it returns
    /// false, the output passes <see cref="OutputBytes"/> or the tool ends; then stops the whole process tree.
    /// </summary>
    private async Task<Run> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        Func<string, bool> line,
        CancellationToken cancellationToken
    )
    {
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var process = _factory.Start(new(command, arguments) { WorkingDirectory = _root, Environment = _environment });
        await using (process.ConfigureAwait(false))
        {
            await using var stop = linked.Token.Register(process.KillTree).ConfigureAwait(false);
            try
            {
                return await ReadAsync(process, line, deadline.Token, linked.Token, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // A cancellation callback that had not run yet is dropped with its registration.
                if (linked.IsCancellationRequested)
                {
                    process.KillTree();
                }
            }
        }
    }

    private static async Task<Run> ReadAsync(
        IProviderProcess process,
        Func<string, bool> line,
        CancellationToken deadline,
        CancellationToken linked,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await process.Input.DisposeAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The tool may already have exited; it reads no input.
        }

        var error = ReadErrorAsync(process.Error);
        var bytes = 0;
        var stopped = false;
        var capped = false;
        var timedOut = new Run(-1, 0, true, false, true, "");
        try
        {
            using var reader = new StreamReader(process.Output, new UTF8Encoding(false), false, 64 * 1024);
            while (await reader.ReadLineAsync(linked).ConfigureAwait(false) is { } text)
            {
                bytes += Encoding.UTF8.GetByteCount(text) + 1;
                if (!line(text))
                {
                    stopped = true;
                    break;
                }

                if (bytes >= OutputBytes)
                {
                    capped = stopped = true;
                    break;
                }
            }

            if (stopped)
            {
                process.KillTree();
            }

            var exitCode = await process.Completion.WaitAsync(linked).ConfigureAwait(false);
            return new(exitCode, bytes, stopped, capped, false, await error.ConfigureAwait(false));
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or IOException
                && deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
            )
        {
            return timedOut with { Bytes = bytes };
        }
        catch (IOException exception) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping the tree closed the pipe under the read.
            throw new OperationCanceledException("search cancelled", exception, cancellationToken);
        }
    }

    /// <summary>The start of the tool's error output; the rest is read and dropped so the tool never blocks on it.</summary>
    private static async Task<string> ReadErrorAsync(Stream stream)
    {
        var kept = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                if (kept.Length < ErrorBytes)
                {
                    await kept.WriteAsync(
                            buffer.AsMemory(0, (int)Math.Min(read, ErrorBytes - kept.Length)),
                            CancellationToken.None
                        )
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The pipe closes when the tool is stopped.
        }

        return Encoding.UTF8.GetString(kept.ToArray()).Trim();
    }
}
