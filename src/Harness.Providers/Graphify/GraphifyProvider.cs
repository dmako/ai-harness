using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Graphify;
using Harness.Core.Memory;
using Harness.Core.Providers;

namespace Harness.Providers.Graphify;

/// <summary>Semantic queries come exclusively from an external warm graphify-csharp session.</summary>
public sealed class GraphifyProvider : IProvider
{
    public const string ProviderName = "graphify-csharp";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly HashSet<string> TypeKinds = new(
        ["interface", "class", "struct", "record", "record_struct", "enum", "delegate", "type"],
        StringComparer.Ordinal
    );
    private readonly GraphifyOptions _options;
    private readonly IGraphifySessions _sessions;
    private readonly SemaphoreSlim _ensureGate = new(1, 1);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private IGraphifyWatch? _watch;
    private string _session = "";
    private Task _warm = Task.CompletedTask;
    private Task _load = Task.CompletedTask;
    private Task? _close;
    private bool _warming;
    private bool _closed;

    public GraphifyProvider(GraphifyOptions options, IGraphifySessions sessions)
    {
        _options = options with
        {
            ReadyWait = options.ReadyWait > TimeSpan.Zero ? options.ReadyWait : TimeSpan.FromSeconds(3),
            Idle = options.Idle > TimeSpan.Zero ? options.Idle : TimeSpan.FromHours(2),
        };
        _sessions = sessions;
    }

    public string Name => ProviderName;

    public IReadOnlyList<CapabilityInfo> Capabilities() =>
        new[]
        {
            Core.Providers.Capabilities.SymbolSearch,
            Core.Providers.Capabilities.Callers,
            Core.Providers.Capabilities.References,
            Core.Providers.Capabilities.Impact,
        }
            .Select(cap => new CapabilityInfo(
                cap,
                Name,
                "low",
                "low",
                "live",
                "project",
                cap == Core.Providers.Capabilities.SymbolSearch ? null : "exact"
            ))
            .ToArray();

    public void Warm()
    {
        lock (_gate)
        {
            if (_warming || _closed)
            {
                return;
            }

            _warming = true;
            _warm = WarmAsync();
        }
    }

    private async Task WarmAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, deadline.Token);
        try
        {
            await _sessions.CleanAsync(linked.Token).ConfigureAwait(false);
            await _sessions.SweepAsync(_options.Idle, linked.Token).ConfigureAwait(false);
            await _ensureGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (ReadSession().Length > 0 || _watch is not null)
                {
                    return;
                }

                if ((await MineAsync(linked.Token).ConfigureAwait(false)).Count is 0)
                {
                    await StartAsync(linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _ensureGate.Release();
            }
        }
        catch (Exception error) when (Failure(error) || error is OperationCanceledException)
        { /* A query or health probe reports startup failure without delaying the handshake. */
        }
    }

    private async Task StartAsync(CancellationToken token)
    {
        var watch = await _sessions.StartAsync(token).ConfigureAwait(false);
        if (watch is null)
        {
            return;
        }

        _watch = watch;
        _load = WatchLoadAsync(watch);
    }

    private async Task WatchLoadAsync(IGraphifyWatch watch)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);
        try
        {
            while (!watch.Completion.IsCompleted)
            {
                var ready = (await MineAsync(linked.Token).ConfigureAwait(false)).FirstOrDefault(session =>
                    session.Pid == watch.Id && session.Ready
                );
                if (ready is not null)
                {
                    await _sessions.NoteLoadedAsync(ready.UptimeMs, linked.Token).ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
            }
            _ = await watch.Completion.ConfigureAwait(false);
        }
        catch (Exception error) when (Failure(error) || error is OperationCanceledException)
        { /* Optional load estimates do not own the watch lifetime. */
        }
    }

    private async Task<string> EnsureAsync(CancellationToken token)
    {
        if (ReadSession() is { Length: > 0 } cached)
        {
            return cached;
        }

        using var deadline = new CancellationTokenSource(_options.ReadyWait);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token, _lifetime.Token);
        GraphifySession? last = null;
        var entered = false;
        try
        {
            await _ensureGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            while (true)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (ReadSession() is { Length: > 0 } current)
                {
                    return current;
                }

                last = Pick(await MineAsync(linked.Token).ConfigureAwait(false));
                if (last is { Ready: true })
                {
                    if (_watch?.Id == last.Pid)
                    {
                        await _sessions.NoteLoadedAsync(last.UptimeMs, linked.Token).ConfigureAwait(false);
                    }

                    lock (_gate)
                    {
                        _session = last.Id;
                    }

                    return last.Id;
                }
                if (last is null && _watch is null)
                {
                    await StartAsync(linked.Token).ConfigureAwait(false);
                }

                if (_watch is { Completion.IsCompleted: true })
                {
                    _watch = null;
                    throw new IOException("graphify-csharp watch exited: " + _sessions.LastLog());
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested
                && !token.IsCancellationRequested
                && !_lifetime.IsCancellationRequested
            )
        {
            throw new IOException(Loading(last));
        }
        finally
        {
            if (entered)
            {
                _ensureGate.Release();
            }
        }
    }

    public async ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default)
    {
        if (ReadSession() is { Length: > 0 } id)
        {
            return new("ready", "warm session " + Short(id));
        }

        string input;
        try
        {
            input = _sessions.InputDescription();
        }
        catch (Exception error) when (Failure(error))
        {
            return new("unavailable", Safe(error.Message), "choose the solution graphify loads");
        }
        if (!probe)
        {
            return new("idle", "starts on first use (" + input + ")");
        }

        try
        {
            var all = await _sessions.ListAsync(false, cancellationToken).ConfigureAwait(false);
            var mine = Mine(all);
            var stale = all.Count(session =>
                !session.Reachable && (session.Root == _sessions.Root || !Directory.Exists(session.Root))
            );
            if (stale > 0)
            {
                return new(
                    "degraded",
                    stale + " stale graphify session(s) (process gone) for this or a deleted root",
                    "harness-graphify --clean --root " + _sessions.Root
                );
            }

            if (mine.Length > 1)
            {
                return new(
                    "degraded",
                    mine.Length + " warm sessions for this project; one is enough",
                    string.Join(
                        "; ",
                        mine.Where(session => session.Id != Pick(mine)!.Id)
                            .Select(session => "graphify-csharp stop " + Short(session.Id))
                    )
                );
            }

            return Pick(mine) switch
            {
                null => new("idle", "no warm session; the first query starts one (compiles the " + input + ")"),
                { Ready: false } session => new(
                    "degraded",
                    "warm session " + Short(session.Id) + ": " + Summary(session),
                    "wait, or restart it: harness providers sessions restart"
                ),
                { } session => new("ready", "warm session " + Short(session.Id) + ": " + Summary(session)),
            };
        }
        catch (Exception error) when (Failure(error))
        {
            return new(
                "unavailable",
                Safe(error.Message),
                "install it (dotnet tool install -g graphify-csharp) or correct providers.graphify-csharp.command"
            );
        }
    }

    public async Task<ProviderResult> ExecuteAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var limit = request.Limit > 0 ? request.Limit : 20;
        return request.Capability switch
        {
            Core.Providers.Capabilities.SymbolSearch => await SymbolsAsync(request, limit, cancellationToken)
                .ConfigureAwait(false),
            Core.Providers.Capabilities.Callers => await EdgesAsync(request, "callers", limit, cancellationToken)
                .ConfigureAwait(false),
            Core.Providers.Capabilities.References => await EdgesAsync(request, "usages", limit, cancellationToken)
                .ConfigureAwait(false),
            Core.Providers.Capabilities.Impact => await ImpactAsync(request, limit, cancellationToken)
                .ConfigureAwait(false),
            _ => new() { Error = new UnsupportedCapabilityException() },
        };
    }

    private async Task<(IReadOnlyList<Symbol> Symbols, bool More, int Bytes)> FindAsync(
        string query,
        string path,
        int want,
        CancellationToken token
    )
    {
        string[] args =
            path.Length > 0
                ?
                [
                    query,
                    "--limit",
                    Math.Min((long)want * 3, 1000).ToString(CultureInfo.InvariantCulture),
                    "--path",
                    path,
                ]
                : [query, "--limit", Math.Min((long)want * 3, 1000).ToString(CultureInfo.InvariantCulture)];
        var (response, bytes) = await QueryAsync("symbols", args, token).ConfigureAwait(false);
        return (
            DecodeItems<Symbol>(response)
                .Where(symbol => symbol.DeclarationKind is not ("parameter" or "local" or "type_parameter"))
                .ToArray(),
            response.Page?.HasMore is true,
            bytes
        );
    }

    private async Task<ProviderResult> SymbolsAsync(ProviderRequest request, int limit, CancellationToken token)
    {
        try
        {
            var (symbols, more, bytes) = await FindAsync(request.Query, request.Path, limit, token)
                .ConfigureAwait(false);
            return new()
            {
                Bytes = bytes,
                Truncated = more || symbols.Count > limit,
                Items = symbols
                    .Take(limit)
                    .Select(symbol => new ProviderItem
                    {
                        Kind = "symbol",
                        Name = ShortName(symbol.Label),
                        QualifiedName = symbol.Label,
                        Label = symbol.DeclarationKind,
                        Path = symbol.Location?.File,
                        StartLine = symbol.Location?.Line ?? 0,
                        Provider = Name,
                        Tool = "query symbols",
                    })
                    .ToArray(),
            };
        }
        catch (Exception error) when (Failure(error))
        {
            return new() { Bytes = (error as GraphifyQueryException)?.Bytes ?? 0, Error = Error("symbols", error) };
        }
    }

    private async Task<ProviderResult> EdgesAsync(
        ProviderRequest request,
        string verb,
        int limit,
        CancellationToken token
    )
    {
        var bytes = 0;
        var truncated = false;
        List<ProviderItem> items = [];
        try
        {
            var (symbols, _, size) = await FindAsync(request.Query, "", 20, token).ConfigureAwait(false);
            bytes += size;
            var targets = symbols.Where(symbol => Names(symbol.Label, request.Query)).ToArray();
            truncated = targets.Length > 5;
            foreach (var symbol in targets.Take(5))
            {
                var (response, count) = await QueryAsync(
                        verb,
                        ["--symbol", symbol.Id, "--limit", limit.ToString(CultureInfo.InvariantCulture)],
                        token
                    )
                    .ConfigureAwait(false);
                bytes += count;
                truncated |= response.Page?.HasMore is true;
                foreach (
                    var edge in DecodeItems<Edge>(response).Where(edge => request.Covers(edge.Location?.File ?? ""))
                )
                {
                    items.Add(
                        new()
                        {
                            Kind = verb is "usages" ? "match" : "symbol",
                            Name = ShortName(edge.Origin?.Label ?? ""),
                            QualifiedName = edge.Origin?.Label,
                            Label = edge.Relation,
                            Path = edge.Location?.File,
                            StartLine = edge.Location?.Line ?? 0,
                            Snippet = "-> " + ShortName(symbol.Label),
                            Provider = Name,
                            Tool = "query " + verb,
                        }
                    );
                }
            }
        }
        catch (Exception error) when (Failure(error))
        {
            return new()
            {
                Bytes = bytes + ((error as GraphifyQueryException)?.Bytes ?? 0),
                Items = items,
                Truncated = truncated,
                Error = Error(verb, error),
            };
        }
        return new()
        {
            Bytes = bytes,
            Items = items.Take(limit).ToArray(),
            Truncated = truncated || items.Count > limit,
        };
    }

    private async Task<ProviderResult> ImpactAsync(ProviderRequest request, int limit, CancellationToken token)
    {
        var bytes = 0;
        List<ProviderItem> items = [];
        try
        {
            var (symbols, _, size) = await FindAsync(request.Query, "", 20, token).ConfigureAwait(false);
            bytes += size;
            var frontier = symbols
                .Where(symbol => Names(symbol.Label, request.Query))
                .Take(5)
                .Select(symbol => (symbol.Id, Type: TypeKinds.Contains(symbol.DeclarationKind)))
                .ToArray();
            var seen = frontier.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
            for (var depth = 1; depth <= 4 && frontier.Length > 0; depth++)
            {
                List<(string Id, bool Type)> next = [];
                foreach (var target in frontier)
                {
                    var verb = target.Type ? "usages" : "callers";
                    var (response, count) = await QueryAsync(verb, ["--symbol", target.Id, "--limit", "200"], token)
                        .ConfigureAwait(false);
                    bytes += count;
                    foreach (var edge in DecodeItems<Edge>(response))
                    {
                        var origin = edge.Origin;
                        if (origin is { Id.Length: > 0 } && !seen.Add(origin.Id))
                        {
                            continue;
                        }

                        if (origin is { Id.Length: > 0 })
                        {
                            next.Add((origin.Id, TypeKinds.Contains(origin.DeclarationKind)));
                        }

                        var location = origin?.Location is { File.Length: > 0 } definition ? definition : edge.Location;
                        if (location is not { File.Length: > 0 } || !request.Covers(location.File))
                        {
                            continue;
                        }

                        if (items.Count == limit)
                        {
                            return new()
                            {
                                Items = items,
                                Bytes = bytes,
                                Truncated = true,
                            };
                        }

                        var label = string.IsNullOrEmpty(origin?.Label) ? request.Query : origin.Label;
                        items.Add(
                            new()
                            {
                                Kind = "symbol",
                                Name = ShortName(label),
                                QualifiedName = origin?.Label,
                                Label = edge.Relation.Length is 0 ? "calls" : edge.Relation,
                                Path = location.File,
                                StartLine = location.Line,
                                Depth = depth,
                                Provider = Name,
                                Tool = "query " + verb,
                            }
                        );
                    }
                }
                frontier = next.ToArray();
            }
            return new()
            {
                Items = items,
                Bytes = bytes,
                Truncated = frontier.Length > 0,
            };
        }
        catch (Exception error) when (Failure(error))
        {
            return new()
            {
                Items = items,
                Bytes = bytes + ((error as GraphifyQueryException)?.Bytes ?? 0),
                Error = Error("callers", error),
            };
        }
    }

    private async Task<(Response Response, int Bytes)> QueryAsync(
        string verb,
        IReadOnlyList<string> arguments,
        CancellationToken token
    )
    {
        var retried = false;
        while (true)
        {
            var id = await EnsureAsync(token).ConfigureAwait(false);
            await _sessions.TouchAsync(id, token).ConfigureAwait(false);
            Exception? error = null;
            Response? response = null;
            var bytes = 0;
            try
            {
                var output = await _sessions
                    .RunAsync(["query", verb, .. arguments, "--instance", id, "--json", "--no-progress"], token)
                    .ConfigureAwait(false);
                bytes = output.Length;
                try
                {
                    response =
                        JsonSerializer.Deserialize<Response>(output, JsonOptions)
                        ?? throw new JsonException("null query response");
                }
                catch (JsonException json)
                {
                    throw new InvalidDataException("query " + verb + ": unexpected output: " + json.Message, json);
                }
                if (response.Success)
                {
                    if (response.Scope?.InputDiscoveryComplete is { } complete)
                    {
                        await _sessions
                            .NoteDiscoveryAsync(
                                id,
                                complete
                                    ? ""
                                    : DiscoveryReason(
                                        (response.Diagnostics ?? []).Select(diagnostic => diagnostic.Message)
                                    ),
                                token
                            )
                            .ConfigureAwait(false);
                    }

                    return (response, bytes);
                }
                error = new IOException(
                    response.Error is { } failure ? failure.Code + ": " + failure.Message : "query failed"
                );
            }
            catch (Exception failure) when (Failure(failure))
            {
                error = failure;
            }
            if (!retried && !(await MineAsync(token).ConfigureAwait(false)).Any(session => session.Id == id))
            {
                retried = true;
                lock (_gate)
                {
                    if (_session == id)
                    {
                        _session = "";
                    }
                }
                continue;
            }
            throw new GraphifyQueryException(verb, bytes, error);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            return new(_close ??= CloseAsync());
        }
    }

    private async Task CloseAsync()
    {
        lock (_gate)
        {
            _closed = true;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _warm.ConfigureAwait(false);
        await _ensureGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _load.ConfigureAwait(false);
            if (_watch is not { } watch || !_options.StopOnExit)
            {
                return;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var owned = (await _sessions.ListAsync(false, deadline.Token).ConfigureAwait(false)).FirstOrDefault(
                    session => session.Pid == watch.Id
                );
                if (owned is not null)
                {
                    await _sessions.StopAsync(owned.Id, deadline.Token).ConfigureAwait(false);
                }

                await watch.Completion.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (Failure(error) || error is OperationCanceledException)
            {
                watch.Kill();
                await watch.Completion.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _watch = null;
            _ensureGate.Release();
            _ensureGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private string ReadSession()
    {
        lock (_gate)
        {
            return _session;
        }
    }

    private async Task<IReadOnlyList<GraphifySession>> MineAsync(CancellationToken token) =>
        Mine(await _sessions.ListAsync(false, token).ConfigureAwait(false));

    private GraphifySession[] Mine(IReadOnlyList<GraphifySession> sessions) =>
        sessions
            .Where(session =>
                session.Reachable
                && string.Equals(
                    session.Root,
                    _sessions.Root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
                )
            )
            .ToArray();

    private static GraphifySession? Pick(IReadOnlyList<GraphifySession> sessions) =>
        sessions.FirstOrDefault(session => session.Ready) ?? (sessions.Count > 0 ? sessions[0] : null);

    private ProviderException Error(string verb, Exception error) =>
        error is GraphifyQueryException query
            ? new(Name, Safe(query.InnerException!.Message), "query " + query.Verb, innerException: query)
            : new(Name, Safe(error.Message), "query " + verb, innerException: error);

    private static bool Failure(Exception error) =>
        error
            is IOException
                or InvalidDataException
                or JsonException
                or TimeoutException
                or System.ComponentModel.Win32Exception
                or GraphifyQueryException;

    private static string Safe(string value) => CredentialRedactor.Redact(value);

    private static string Short(string value) => value.Length > 8 ? value[..8] : value;

    private static string ShortName(string label) => label.Split('(')[0].Split('.')[^1];

    private static bool Names(string label, string query) =>
        label.Split('(')[0] is var name && (name == query || name.EndsWith("." + query, StringComparison.Ordinal));

    private static string Loading(GraphifySession? session) =>
        "warm session is still loading (stage "
        + (string.IsNullOrEmpty(session?.Stage) ? "starting" : session.Stage)
        + (
            session?.Remaining() > TimeSpan.Zero
                ? ", about "
                    + Seconds(session.Remaining())
                    + " left (the last load took "
                    + Seconds(TimeSpan.FromMilliseconds(session.LoadTookMs))
                    + ")"
                : ""
        )
        + "); retry shortly, the other providers answer meanwhile";

    private static string Seconds(TimeSpan span) =>
        Math.Round(span.TotalSeconds, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "s";

    public static string Summary(GraphifySession session)
    {
        List<string> parts = [];
        var loading = "loading" + (session.Stage.Length > 0 ? " (" + session.Stage + ")" : "");
        if (session.Remaining() > TimeSpan.Zero)
        {
            loading += ", about " + Seconds(session.Remaining()) + " left";
        }

        parts.Add(
            (session.Reachable, session.Ready) switch
            {
                (false, _) => "not reachable (process gone)",
                (_, true) => "ready",
                _ => loading,
            }
        );
        if (session.MemoryBytes > 0)
        {
            parts.Add(((double)session.MemoryBytes / (1L << 30)).ToString("F1", CultureInfo.InvariantCulture) + " GB");
        }

        if (session.UptimeMs > 0)
        {
            parts.Add("up " + Age(TimeSpan.FromMilliseconds(session.UptimeMs)));
        }

        if (
            DateTimeOffset.TryParse(
                session.LastQuery,
                CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var at
            )
        )
        {
            parts.Add("queried " + Age(DateTimeOffset.UtcNow - at) + " ago");
        }

        if (session.DiscoveryIncomplete.Length > 0)
        {
            parts.Add("discovery incomplete (" + session.DiscoveryIncomplete + "): queries run slow");
        }

        parts.Add(session.StartedBy.Length > 0 ? "started by " + session.StartedBy : "started outside harness");
        return string.Join(", ", parts);
    }

    private static string Age(TimeSpan span) =>
        span switch
        {
            { TotalHours: >= 48 } => ((int)span.TotalDays).ToString(CultureInfo.InvariantCulture) + "d",
            { TotalHours: >= 1 } => ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + "h",
            { TotalMinutes: >= 1 } => ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m",
            _ => ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s",
        };

    public static string DiscoveryReason(IEnumerable<string> diagnostics)
    {
        var diagnostic = diagnostics.FirstOrDefault(text =>
            text.Contains("discovery", StringComparison.Ordinal)
            || text.Contains("provenance", StringComparison.Ordinal)
        );
        if (diagnostic is null)
        {
            return "graphify gave no reason";
        }

        var match = Regex.Match(
            diagnostic,
            @"([\w.\-]+\.(?:targets|props))'",
            RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1)
        );
        if (match.Success)
        {
            return match.Groups[1].Value + " adds items graphify cannot trace";
        }

        return diagnostic.Length > 160 ? diagnostic[..160] + "…" : diagnostic;
    }

    private static IEnumerable<T> DecodeItems<T>(Response response)
        where T : class
    {
        foreach (var item in response.Items ?? [])
        {
            T? decoded;
            try
            {
                decoded = item.Deserialize<T>(JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (decoded is not null)
            {
                yield return decoded;
            }
        }
    }

    private sealed record Location(string File = "", int Line = 0);

    private sealed record Symbol(
        string Id = "",
        string Label = "",
        string DeclarationKind = "",
        Location? Location = null
    );

    private sealed record Edge(string Relation = "", Location? Location = null, Symbol? Origin = null);

    private sealed record Page(bool HasMore = false);

    private sealed record Scope(bool? InputDiscoveryComplete = null);

    private sealed record Diagnostic(string Message = "");

    private sealed record RemoteError(string Code = "", string Message = "");

    private sealed record Response(
        bool Success = false,
        IReadOnlyList<JsonElement>? Items = null,
        Page? Page = null,
        Scope? Scope = null,
        IReadOnlyList<Diagnostic>? Diagnostics = null,
        RemoteError? Error = null
    );
}
