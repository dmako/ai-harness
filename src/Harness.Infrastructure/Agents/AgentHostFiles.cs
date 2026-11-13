using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Providers;
using Harness.Infrastructure.Processes;

namespace Harness.Infrastructure.Agents;

/// <summary>
/// What an agent host (harness-agents serve) shows of itself beside the project database, in agents/: its state
/// (&lt;key&gt;.json: client, task, session, process, status) and its events (&lt;key&gt;.jsonl, one JSON object per line
/// with a sequence number). Commands and live events go through its named pipe (see <see cref="Pipe"/>), open to the
/// current user only.
/// </summary>
public static class AgentHostFiles
{
    public const string PipePrefix = "harness-agent-";

    /// <summary>The statuses a host passes through; closed and failed are final.</summary>
    public const string Starting = "starting";
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Closed = "closed";
    public const string Failed = "failed";

    public static string Directory(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "agents");

    public static string StatePath(string directory, string key) => Path.Combine(directory, key + ".json");

    public static string LogPath(string directory, string key) => Path.Combine(directory, key + ".jsonl");

    /// <summary>
    /// The host's pipe name: harness-agent-&lt;key&gt; on Windows. Elsewhere .NET makes the pipe a Unix socket at
    /// $TMPDIR/CoreFxPipe_&lt;name&gt;, and socket paths are limited to 104 bytes on macOS (108 on Linux); macOS's long
    /// TMPDIR (/var/folders/../T/) left no room for the full key, so the name there is ha- and the key's first 16 digits.
    /// </summary>
    public static string Pipe(string key) => OperatingSystem.IsWindows() ? PipePrefix + key : "ha-" + key[..16];

    /// <summary>A host key: 32 lowercase hex digits, the only form a pipe or file name is built from.</summary>
    public static bool IsKey(string key) => key.Length is 32 && key.All(char.IsAsciiHexDigitLower);

    public static string NewKey() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates the agents folder, which keeps itself out of Git: its own .gitignore ignores everything in it, while
    /// projects' .harness/.gitignore (older ones included) only the database.
    /// </summary>
    public static void Prepare(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        var ignore = Path.Combine(directory, ".gitignore");
        if (!File.Exists(ignore))
        {
            File.WriteAllText(
                ignore,
                "# Managed by harness: agent hosts' state and logs stay local.\n*\n",
                new UTF8Encoding(false)
            );
        }
    }

    /// <summary>Writes a host's state through a temporary file, so a reader never sees half of it.</summary>
    public static void WriteState(string directory, string key, JsonObject state)
    {
        Prepare(directory);
        var path = StatePath(directory, key);
        var pending = path + ".tmp";
        File.WriteAllText(pending, state.ToJsonString(), new UTF8Encoding(false));
        File.Move(pending, path, overwrite: true);
    }

    /// <summary>
    /// A host's state, or null when it has none. On Windows a read that meets the host replacing the file fails for a
    /// moment (sharing violation, access denied), so a failed read is tried again a few times.
    /// </summary>
    public static JsonObject? ReadState(string directory, string key)
    {
        var path = StatePath(directory, key);
        const int Attempts = 4;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                if (!File.Exists(path) && !File.Exists(path + ".tmp"))
                {
                    return null;
                }
            }
            if (attempt < Attempts)
            {
                Thread.Sleep(25);
            }
        }
        return null;
    }

    /// <summary>Every host state in a project's agents folder, the newest first.</summary>
    public static IReadOnlyList<JsonObject> States(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }
        return System
            .IO.Directory.EnumerateFiles(directory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(IsKey)
            .Select(key => ReadState(directory, key))
            .OfType<JsonObject>()
            .OrderByDescending(state => Text(state, "started_at"), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The events a host logged after sequence number <paramref name="after"/>.</summary>
    public static IReadOnlyList<JsonObject> Events(string directory, string key, long after = 0)
    {
        var path = LogPath(directory, key);
        if (!File.Exists(path))
        {
            return [];
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        List<JsonObject> events = [];
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject item && Sequence(item) > after)
                {
                    events.Add(item);
                }
            }
            catch (JsonException)
            {
                // A line still being written.
            }
        }
        return events;
    }

    /// <summary>Whether the host is still running: its state is not final and its process is alive.</summary>
    public static bool Alive(JsonObject state)
    {
        if (Text(state, "status") is Closed or Failed || state["pid"] is not JsonValue value)
        {
            return false;
        }
        if (!value.TryGetValue<int>(out var pid))
        {
            return false;
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static long Sequence(JsonObject item) =>
        item["seq"] is JsonValue value && value.TryGetValue<long>(out var seq) ? seq : 0;

    public static string Text(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    public static string Now() =>
        DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>A connection to an agent host's pipe: its events as they come, and commands to it.</summary>
public sealed class AgentHostClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _write = new(1, 1);

    private AgentHostClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    /// <summary>Connects to the host <paramref name="key"/>; null when no host listens there.</summary>
    public static async Task<AgentHostClient?> ConnectAsync(
        string key,
        TimeSpan timeout,
        CancellationToken cancellationToken = default
    )
    {
        if (!AgentHostFiles.IsKey(key))
        {
            return null;
        }
        var pipe = new NamedPipeClientStream(
            ".",
            AgentHostFiles.Pipe(key),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await pipe.ConnectAsync(deadline.Token);
            return new AgentHostClient(pipe);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or TimeoutException or IOException
                && !cancellationToken.IsCancellationRequested
            )
        {
            await pipe.DisposeAsync();
            return null;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    /// <summary>The host's events: everything it logged so far, then each new one; ends when the host goes.</summary>
    public async IAsyncEnumerable<JsonObject> EventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        while (true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(cancellationToken);
            }
            catch (IOException)
            {
                yield break;
            }
            if (line is null)
            {
                yield break;
            }
            JsonObject? item = null;
            try
            {
                item = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // Not an event.
            }
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    /// <summary>Sends a command: {"op":"send","text"}, {"op":"stop"}, {"op":"approve","id","allow"} or {"op":"close"}.</summary>
    public async Task SendAsync(JsonObject command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var bytes = new UTF8Encoding(false).GetBytes(command.ToJsonString() + "\n");
        await _write.WaitAsync(cancellationToken);
        try
        {
            await _pipe.WriteAsync(bytes, cancellationToken);
            await _pipe.FlushAsync(cancellationToken);
        }
        finally
        {
            _write.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _pipe.DisposeAsync();
        _write.Dispose();
    }
}

/// <summary>Starts an agent host detached, so it outlives whoever started it; its standard error goes to a file.</summary>
public static class AgentHostLauncher
{
    /// <summary>
    /// The host executable: HARNESS_AGENTS_PATH when set, else harness-agents beside this process's executable.
    /// </summary>
    public static string Executable()
    {
        if (Environment.GetEnvironmentVariable("HARNESS_AGENTS_PATH") is { Length: > 0 } configured)
        {
            return configured;
        }
        var name = OperatingSystem.IsWindows() ? "harness-agents.exe" : "harness-agents";
        var directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var beside = Path.Combine(directory, name);
        return File.Exists(beside) ? beside : Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>Starts the host with these arguments; returns its process id.</summary>
    public static int Start(IReadOnlyList<string> arguments, string workingDirectory, string errorLog)
    {
        AgentHostFiles.Prepare(Path.GetDirectoryName(errorLog)!);
        var watch = DetachedProcess.Start(
            new ProviderProcessOptions(Executable(), arguments)
            {
                WorkingDirectory = workingDirectory,
                AllowDetachedChildren = true,
            },
            errorLog
        );
        return watch.Id;
    }
}
