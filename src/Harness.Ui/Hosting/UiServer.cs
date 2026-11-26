using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Harness.Ui.Hosting;

/// <summary>What answers on the UI's address.</summary>
public enum UiServerProbe
{
    /// <summary>A harness UI API: GET /api/projects answers {"projects": [...]}.</summary>
    Harness,

    /// <summary>Nothing listens there (refused, or no answer in time).</summary>
    Nothing,

    /// <summary>Something else answers: another program holds the port.</summary>
    Foreign,
}

/// <summary>Why the desktop app could not reach a harness UI API; its message is shown in the window as it is.</summary>
public sealed class UiServerException : Exception
{
    public UiServerException() { }

    public UiServerException(string message)
        : base(message) { }

    public UiServerException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>A started <c>harness ui</c>: whether and how it ended, its last output lines, and how to stop it.</summary>
public interface IServerProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    IReadOnlyList<string> LastLines { get; }

    /// <summary>Stops it and everything it started.</summary>
    void Stop();
}

/// <summary>Starts a program with its arguments, without a console window (a test passes a fake one).</summary>
public interface IServerProcessStarter
{
    IServerProcess Start(string file, IReadOnlyList<string> arguments);
}

/// <summary>
/// The desktop app's server (TASK-0214): it connects to a harness UI API already answering on its address, or, on a
/// loopback address where nothing answers, starts <c>harness ui --no-open --addr &lt;address&gt;</c> (from its own folder,
/// else from PATH), waits until it answers and connects to it. A server it started is stopped with the app.
/// </summary>
public sealed class UiServer(HttpMessageHandler handler, IServerProcessStarter starter) : IDisposable
{
    private readonly HttpClient _probe = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>How long one probe waits for an answer.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a started server may take to answer.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The pause between probes while it starts.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Where the app's own files are (a harness beside it is preferred).</summary>
    public string AppDirectory { get; init; } = AppContext.BaseDirectory;

    /// <summary>The PATH searched when there is none beside the app.</summary>
    public string? SearchPath { get; init; } = Environment.GetEnvironmentVariable("PATH");

    /// <summary>The server this app started, if it did; <see cref="Dispose"/> stops it.</summary>
    public IServerProcess? Started { get; private set; }

    /// <summary>
    /// Makes sure a harness UI API answers on <paramref name="server"/>: connects to a running one, else starts one
    /// (loopback only) and waits for it. <paramref name="status"/> hears what it is doing.
    /// </summary>
    /// <exception cref="UiServerException">Nothing usable answers there; the message says what happened.</exception>
    public async Task ConnectAsync(Uri server, IProgress<string>? status, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(server);
        var address = server.GetLeftPart(UriPartial.Authority);
        status?.Report($"Connecting to harness ui at {address}…");
        switch (await ProbeAsync(server, token))
        {
            case UiServerProbe.Harness:
                return;
            case UiServerProbe.Foreign:
                throw new UiServerException(
                    $"Something other than harness ui answers at {address}. Stop the program using that port, or start the "
                        + "desktop app with another address (its first argument or HARNESS_UI_ADDR)."
                );
            default:
                break;
        }
        if (!IsLoopback(server))
        {
            throw new UiServerException(
                $"No harness ui answers at {address}. It is not on this computer, so the app does not start one: start "
                    + "harness ui there, or give the app a local address."
            );
        }
        var harness = Locate(AppDirectory, SearchPath, OperatingSystem.IsWindows());
        var arguments = new[] { "ui", "--no-open", "--addr", server.Authority };
        var command = $"{harness ?? "harness"} {string.Join(' ', arguments)}";
        if (harness is null)
        {
            throw new UiServerException(
                $"No harness ui answers at {address}, and harness was not found beside the app or on PATH to start one. "
                    + $"Install harness, or start it yourself: {command}"
            );
        }
        status?.Report($"Starting harness ui at {address}…");
        IServerProcess process;
        try
        {
            process = starter.Start(harness, arguments);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            throw new UiServerException($"Could not start harness ui at {address} ({command}): {exception.Message}", exception);
        }
        Started = process;
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (process.HasExited)
            {
                Started = null;
                var exited = Failure($"harness ui exited with code {process.ExitCode} before it answered at {address}.", command, process);
                process.Dispose();
                throw exited;
            }
            if (await ProbeAsync(server, token) is UiServerProbe.Harness)
            {
                return;
            }
            if (deadline.Elapsed >= StartTimeout)
            {
                var late = Failure($"harness ui did not answer at {address} within {StartTimeout.TotalSeconds:0} s.", command, process);
                Stop();
                throw late;
            }
            await Task.Delay(PollInterval, token);
        }
    }

    /// <summary>What answers on <paramref name="server"/>, asked once with <see cref="ProbeTimeout"/>.</summary>
    public async Task<UiServerProbe> ProbeAsync(Uri server, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "/api/projects"));
            request.Headers.Add("X-Harness-UI", "1");
            using var response = await _probe.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return response.StatusCode is HttpStatusCode.OK && IsProjects(body) ? UiServerProbe.Harness : UiServerProbe.Foreign;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return UiServerProbe.Nothing;
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            return UiServerProbe.Nothing;
        }
        catch (HttpRequestException)
        {
            // it accepted the connection but did not speak HTTP as harness does
            return UiServerProbe.Foreign;
        }
    }

    /// <summary>harness (harness.exe on Windows) beside the app, else the first on <paramref name="path"/>; null if none.</summary>
    public static string? Locate(string appDirectory, string? path, bool windows)
    {
        var name = windows ? "harness.exe" : "harness";
        var directories = new[] { appDirectory }.Concat(
            (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        );
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>Whether the address is this computer's (a loopback IP or localhost).</summary>
    public static bool IsLoopback(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.IsLoopback || (IPAddress.TryParse(server.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Stops the server this app started, if any.</summary>
    public void Stop()
    {
        if (Started is { } process)
        {
            Started = null;
            process.Stop();
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        _probe.Dispose();
    }

    private static bool IsProjects(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind is JsonValueKind.Object
                && document.RootElement.TryGetProperty("projects", out var projects)
                && projects.ValueKind is JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static UiServerException Failure(string what, string command, IServerProcess process)
    {
        var lines = process.LastLines;
        return new UiServerException(
            $"{what}\nCommand: {command}" + (lines.Count > 0 ? "\nIts last output:\n" + string.Join('\n', lines) : "\nIt printed nothing.")
        );
    }
}

/// <summary>Starts a real process: no console window, its output kept (the last lines) for an error message.</summary>
public sealed class ServerProcessStarter : IServerProcessStarter
{
    public IServerProcess Start(string file, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var started = new StartedProcess(process);
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("the process did not start");
            }
        }
        catch
        {
            started.Dispose();
            throw;
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return started;
    }

    private sealed class StartedProcess : IServerProcess
    {
        private const int Kept = 20;
        private readonly Process _process;
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public StartedProcess(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) => Keep(e.Data);
            _process.ErrorDataReceived += (_, e) => Keep(e.Data);
        }

        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;

        public IReadOnlyList<string> LastLines
        {
            get
            {
                lock (_gate)
                {
                    return [.. _lines];
                }
            }
        }

        public void Stop()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
                // it was never started or has already gone
            }
        }

        public void Dispose() => _process.Dispose();

        private void Keep(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }
            lock (_gate)
            {
                _lines.Enqueue(line);
                while (_lines.Count > Kept)
                {
                    _lines.Dequeue();
                }
            }
        }
    }
}
