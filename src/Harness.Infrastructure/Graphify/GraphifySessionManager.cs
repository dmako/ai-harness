using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Graphify;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Graphify;

public sealed class GraphifySessionManager : IGraphifySessions, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly GraphifyOptions _options;
    private readonly IProviderProcessFactory _processes;
    private readonly TimeProvider _clock;
    private readonly GraphifyRecords _records;
    private readonly string _recordsPath;
    private readonly SemaphoreSlim _listGate = new(1, 1);
    private readonly object _observations = new();
    private readonly Dictionary<string, DateTimeOffset> _touched = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _discovery = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pids = new(StringComparer.Ordinal);
    private byte[]? _recent;
    private DateTimeOffset _listed;

    public GraphifySessionManager(
        GraphifyOptions options,
        IProviderProcessFactory? processes = null,
        TimeProvider? clock = null
    )
    {
        Root = GraphifyDiscovery.Canonical(options.Root);
        _options = options with { Root = Root };
        _processes = processes ?? new ProviderProcessFactory();
        _clock = clock ?? TimeProvider.System;
        _recordsPath =
            options.RecordsPath.Length > 0
                ? Path.GetFullPath(options.RecordsPath)
                : Path.Combine(Path.GetDirectoryName(ConfigurationPaths.GlobalPath())!, "graphify-sessions.json");
        _records = new(_recordsPath);
    }

    public string Root { get; }
    public string SessionDirectory
    {
        get
        {
            if (_options.SessionDirectory.Length > 0)
            {
                return Path.GetFullPath(_options.SessionDirectory);
            }

            var folder = OperatingSystem.IsWindows()
                ? Environment.SpecialFolder.LocalApplicationData
                : Environment.SpecialFolder.ApplicationData;
            return Path.Combine(Environment.GetFolderPath(folder), "Graphify.CSharp", "sessions");
        }
    }

    public string InputDescription() => GraphifyDiscovery.WatchArguments(Root, _options.WatchArguments).Description;

    public async Task<byte[]> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        IProviderProcess process;
        try
        {
            process = _processes.Start(
                new(_options.Command, [.. _options.CommandArguments, .. arguments])
                {
                    WorkingDirectory = Root,
                    Environment = _options.Environment,
                }
            );
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new IOException("command not found: " + _options.Command, error);
        }
        await using (process.ConfigureAwait(false))
        {
            await process.Input.DisposeAsync().ConfigureAwait(false);
            var output = ReadAsync(process.Output, 16 * 1024 * 1024, tail: false, process, linked.Token);
            var stderr = ReadAsync(process.Error, 64 * 1024, tail: true, process, linked.Token);
            try
            {
                var exit = await process.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
                await Task.WhenAll(output, stderr).ConfigureAwait(false);
                var bytes = await output.ConfigureAwait(false);
                if (exit is not 0 && bytes.Length is 0)
                {
                    var message = Encoding.UTF8.GetString(await stderr.ConfigureAwait(false)).Trim();
                    throw new IOException(
                        CredentialRedactor.Redact(message.Length is 0 ? "exit status " + exit : message.Split('\n')[0])
                    );
                }
                return bytes;
            }
            catch (Exception error)
            {
                process.KillTree();
                try
                {
                    await Task.WhenAll(output, stderr).ConfigureAwait(false);
                }
                catch (Exception readError)
                    when (readError is IOException or OperationCanceledException or InvalidDataException)
                { /* Observe both readers after termination. */
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("graphify command timed out", error);
                }

                throw;
            }
        }
    }

    public async Task<IReadOnlyList<GraphifySession>> ListAsync(bool recent, CancellationToken cancellationToken)
    {
        byte[] bytes;
        if (!recent)
        {
            bytes = await RunAsync(["ps", "--json"], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _listGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_recent is null || _clock.GetUtcNow() - _listed >= TimeSpan.FromSeconds(5))
                {
                    _recent = await RunAsync(["ps", "--json"], cancellationToken).ConfigureAwait(false);
                    _listed = _clock.GetUtcNow();
                }
                bytes = _recent;
            }
            finally
            {
                _listGate.Release();
            }
        }
        SessionList list;
        try
        {
            list =
                HarnessJson.Deserialize<SessionList>(bytes, JsonOptions)
                ?? throw new JsonException("null session list");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("ps: unexpected output: " + error.Message, error);
        }
        var records = _records.Load();
        var sessions = new List<GraphifySession>();
        foreach (var row in list.Sessions ?? [])
        {
            var root = row.RepositoryRoot.Length is 0 ? "" : GraphifyDiscovery.Canonical(row.RepositoryRoot);
            var record = records["sessions"]![row.SessionId];
            var starting = records["starting"]![root];
            var by = record?["started_by"]?.GetValue<string>() ?? "";
            if (record is null && starting?["pid"]?.GetValue<int>() == row.ProcessId)
            {
                by = starting["by"]?.GetValue<string>() ?? "";
            }

            lock (_observations)
            {
                _pids[row.SessionId] = row.ProcessId;
            }

            sessions.Add(
                new()
                {
                    Id = row.SessionId,
                    Pid = row.ProcessId,
                    Reachable = row.Reachability is "reachable",
                    State = row.LifecycleState,
                    Ready = row.Ready,
                    Stage = row.Stage,
                    Root = root,
                    Input = row.InputPath,
                    MemoryBytes = row.WorkingSetBytes,
                    UptimeMs = row.UptimeMs,
                    StartedBy = by,
                    LastQuery = record?["last_query"]?.GetValue<string>() ?? "",
                    DiscoveryIncomplete = record?["discovery_incomplete"]?.GetValue<string>() ?? "",
                    LoadTookMs = records["load_ms"]![root]?.GetValue<long>() ?? 0,
                }
            );
        }
        return sessions
            .OrderBy(session => session.Root, StringComparer.Ordinal)
            .ThenBy(session => session.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IGraphifyWatch?> StartAsync(CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? Root.ToUpperInvariant() : Root)
            )
        );
        await using var lease = await GraphifyRecords
            .LockAsync(
                Path.Combine(Path.GetDirectoryName(_recordsPath)!, "graphify-start-" + key + ".lock"),
                TimeSpan.FromSeconds(5),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (
            (await ListAsync(false, cancellationToken).ConfigureAwait(false)).Any(session =>
                session.Reachable && GraphifyDiscovery.PathComparer.Equals(session.Root, Root)
            )
        )
        {
            return null;
        }

        var pending = _records.Load()["starting"]![Root];
        if (pending?["pid"]?.GetValue<int>() is { } pid && ProcessAlive(pid))
        {
            return null;
        }

        var (arguments, _) = GraphifyDiscovery.WatchArguments(Root, _options.WatchArguments);
        var log = LogPath();
        StorageFiles.CreateDirectory(Path.GetDirectoryName(log)!);
        var watch = DetachedProcess.Start(
            new(
                _options.Command,
                [.. _options.CommandArguments, "watch", "--root", Root, "--no-progress", .. arguments]
            )
            {
                WorkingDirectory = Root,
                Environment = _options.Environment,
            },
            log
        );
        StorageFiles.OwnerOnly(log);
        await _records
            .UpdateAsync(
                records =>
                    records["starting"]![Root] = new JsonObject
                    {
                        ["by"] = _options.StartedBy,
                        ["at"] = Now(),
                        ["pid"] = watch.Id,
                    },
                CancellationToken.None
            )
            .ConfigureAwait(false);
        await ChangedAsync().ConfigureAwait(false);
        return watch;
    }

    public async Task StopAsync(string id, CancellationToken cancellationToken)
    {
        int pid;
        lock (_observations)
        {
            pid = _pids.GetValueOrDefault(id);
        }
        var stopped = false;
        try
        {
            await RunAsync(["stop", id, "--json"], cancellationToken).ConfigureAwait(false);
            stopped = true;
        }
        finally
        {
            await ChangedAsync().ConfigureAwait(false);
            await _records
                .UpdateAsync(
                    records =>
                    {
                        records["sessions"]!.AsObject().Remove(id);
                        if (stopped && pid > 0)
                        {
                            var starting = records["starting"]!.AsObject();
                            foreach (
                                var root in starting
                                    .Where(row => row.Value?["pid"]?.GetValue<int>() == pid)
                                    .Select(row => row.Key)
                                    .ToArray()
                            )
                            {
                                starting.Remove(root);
                            }
                        }
                    },
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
    }

    public async Task<int> CleanAsync(CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var session in await ListAsync(false, cancellationToken).ConfigureAwait(false))
        {
            if (
                session.Reachable
                || ProcessAlive(session.Pid)
                || session.Id.Length is 0
                || session.Id is "." or ".."
                || session.Id.IndexOfAny(['/', '\\', ':', '\0']) >= 0
            )
            {
                continue;
            }

            if (!GraphifyDiscovery.PathComparer.Equals(session.Root, Root) && Directory.Exists(session.Root))
            {
                continue;
            }

            var path = Path.Combine(SessionDirectory, session.Id + ".json");
            try
            {
                var before = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                using var data = JsonDocument.Parse(before);
                if (
                    data.RootElement.GetProperty("session_id").GetString() != session.Id
                    || data.RootElement.GetProperty("process_id").GetInt32() != session.Pid
                    || ProcessAlive(session.Pid)
                )
                {
                    continue;
                }

                var current = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!before.AsSpan().SequenceEqual(current))
                {
                    continue;
                }

                File.Delete(path);
                removed++;
            }
            catch (Exception error)
                when (error
                        is IOException
                            or UnauthorizedAccessException
                            or JsonException
                            or KeyNotFoundException
                            or InvalidOperationException
                )
            { /* A changed, busy or unverifiable descriptor is preserved. */
            }
        }
        if (removed > 0)
        {
            await ChangedAsync().ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<IReadOnlyList<string>> SweepAsync(TimeSpan idle, CancellationToken cancellationToken)
    {
        var sessions = await ListAsync(false, cancellationToken).ConfigureAwait(false);
        List<string> stopped = [];
        foreach (var session in sessions)
        {
            if (
                session.StartedBy.Length is 0
                || !session.Reachable
                || !DateTimeOffset.TryParse(
                    session.LastQuery,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var at
                )
                || _clock.GetUtcNow() - at < idle
            )
            {
                continue;
            }

            try
            {
                await StopAsync(session.Id, cancellationToken).ConfigureAwait(false);
                stopped.Add(session.Id);
            }
            catch (IOException)
            { /* Other sessions can still be swept. */
            }
        }
        var live = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);
        await _records
            .UpdateAsync(
                records =>
                {
                    foreach (
                        var id in records["sessions"]!
                            .AsObject()
                            .Select(pair => pair.Key)
                            .Where(id => !live.Contains(id))
                            .ToArray()
                    )
                    {
                        records["sessions"]!.AsObject().Remove(id);
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);
        return stopped;
    }

    public Task TouchAsync(string id, CancellationToken cancellationToken)
    {
        int pid;
        lock (_observations)
        {
            var now = _clock.GetUtcNow();
            if (_touched.TryGetValue(id, out var at) && now - at < TimeSpan.FromMinutes(1))
            {
                return Task.CompletedTask;
            }

            _touched[id] = now;
            pid = _pids.GetValueOrDefault(id);
        }
        return _records.UpdateAsync(
            records =>
            {
                var sessions = records["sessions"]!.AsObject();
                var record = sessions[id] as JsonObject ?? new();
                record["last_query"] = Now();
                if (
                    records["starting"]![Root] is JsonObject starting
                    && starting["pid"]?.GetValue<int>() == pid
                    && pid > 0
                )
                {
                    record["started_by"] = starting["by"]?.DeepClone();
                    records["starting"]!.AsObject().Remove(Root);
                }
                sessions[id] = record.Parent is null ? record : record.DeepClone();
            },
            cancellationToken
        );
    }

    public Task NoteLoadedAsync(long milliseconds, CancellationToken cancellationToken) =>
        milliseconds > 0
            ? _records.UpdateAsync(records => records["load_ms"]![Root] = milliseconds, cancellationToken)
            : Task.CompletedTask;

    public Task NoteDiscoveryAsync(string id, string reason, CancellationToken cancellationToken)
    {
        lock (_observations)
        {
            if (_discovery.TryGetValue(id, out var last) && last == reason)
            {
                return Task.CompletedTask;
            }

            _discovery[id] = reason;
        }
        return _records.UpdateAsync(
            records =>
            {
                var record = records["sessions"]![id] as JsonObject;
                if (record is null)
                {
                    record = new();
                    records["sessions"]![id] = record;
                }
                if (reason.Length is 0)
                {
                    record.Remove("discovery_incomplete");
                }
                else
                {
                    record["discovery_incomplete"] = reason;
                }
            },
            cancellationToken
        );
    }

    public string LastLog()
    {
        var path = LogPath();
        try
        {
            using var file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            if (file.Length > 8192)
            {
                file.Seek(-8192, SeekOrigin.End);
            }

            using var reader = new StreamReader(file);
            var tail = reader.ReadToEnd().Trim();
            return tail.Length is 0 ? "see " + path : CredentialRedactor.Redact(tail.Split('\n')[^1]);
        }
        catch (IOException)
        {
            return "see " + path;
        }
    }

    public ValueTask DisposeAsync()
    {
        _listGate.Dispose();
        return ValueTask.CompletedTask;
    }

    public static bool ProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return true; // Unknown identity cannot authorize descriptor removal.
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return true;
        }
    }

    private string Now() =>
        _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private string LogPath() =>
        Path.Combine(
            Path.GetDirectoryName(_recordsPath)!,
            "graphify-logs",
            Root.Trim('/', '\\').Replace('/', '_').Replace('\\', '_').Replace(':', '_') + ".log"
        );

    private async Task ChangedAsync()
    {
        await _listGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _recent = null;
        }
        finally
        {
            _listGate.Release();
        }
    }

    private static async Task<byte[]> ReadAsync(
        Stream stream,
        int maximum,
        bool tail,
        IProviderProcess process,
        CancellationToken token
    )
    {
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) is var count && count > 0)
        {
            if (result.Length + count > maximum)
            {
                if (!tail)
                {
                    process.KillTree();
                    throw new InvalidDataException("graphify output exceeded " + maximum + " bytes");
                }
                var old = result.ToArray();
                result.SetLength(0);
                var keep = Math.Min(old.Length, maximum - count);
                result.Write(old.AsSpan(old.Length - keep, keep));
            }
            result.Write(buffer.AsSpan(0, count));
        }
        return result.ToArray();
    }

    internal sealed record SessionList(IReadOnlyList<SessionRow>? Sessions);

    internal sealed record SessionRow(
        string SessionId = "",
        int ProcessId = 0,
        string Reachability = "",
        string LifecycleState = "",
        bool Ready = false,
        string Stage = "",
        string RepositoryRoot = "",
        string InputPath = "",
        long WorkingSetBytes = 0,
        long UptimeMs = 0
    );
}
