using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cli.Memory;
using Harness.Core.Graphify;
using Harness.Core.Serialization;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Projects;
using Harness.Web;

namespace Harness.Cli;

internal sealed class DocsSettings
{
    public string Section { get; init; } = "";
}

internal sealed class DocsCommand(TextWriter output, CliErrorOutput error)
{
    public async Task<int> ExecuteAsync(DocsSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.Section.Length > 0)
        {
            if (UiAssets.Find(settings.Section) is not { } section)
            {
                await error.Writer.WriteLineAsync(
                    "harness docs: no section " + HarnessJson.Serialize(settings.Section) + " (harness docs lists them)"
                );
                return 2;
            }
            await output.WriteLineAsync(section.Body?.Trim());
            return 0;
        }
        foreach (var document in UiAssets.Documents)
        {
            await output.WriteLineAsync("\n" + document.Title + ":");
            foreach (var section in UiAssets.Sections.Where(s => s.Doc == document.Name))
            {
                await output.WriteLineAsync($"  {section.Slug, -40} {section.Title}");
            }
        }
        await output.WriteLineAsync("\nharness docs <section> prints one.");
        return 0;
    }
}

internal sealed class SessionsListSettings
{
    public bool Json { get; init; }
}

internal sealed class SessionStopSettings : ProjectSettings
{
    public string Id { get; init; } = "";
}

internal sealed class SessionsListCommand(SessionCommandRunner runner)
{
    public Task<int> ExecuteAsync(SessionsListSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("list", ".", "", settings.Json, cancellationToken);
}

internal sealed class SessionStartCommand(SessionCommandRunner runner)
{
    public Task<int> ExecuteAsync(ProjectSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("start", settings.Project, "", false, cancellationToken);
}

internal sealed class SessionRestartCommand(SessionCommandRunner runner)
{
    public Task<int> ExecuteAsync(ProjectSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("restart", settings.Project, "", false, cancellationToken);
}

internal sealed class SessionStopCommand(SessionCommandRunner runner)
{
    public Task<int> ExecuteAsync(SessionStopSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("stop", settings.Project, settings.Id, false, cancellationToken);
}

internal sealed class SessionCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    public async Task<int> RunAsync(string verb, string directory, string id, bool json, CancellationToken token)
    {
        try
        {
            var location = await lifecycle.LocateAsync(directory, allowNonGit: true, token);
            var (command, watch) = GraphifyDiscovery.FromConfig(location.Configuration.Value.Providers);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(verb is "list" ? 20 : 30));
            await using var manager = new GraphifySessionManager(
                new(location.Root, command)
                {
                    WatchArguments = watch,
                    RecordsPath = Path.Combine(
                        Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!,
                        "graphify-sessions.json"
                    ),
                    StartedBy = "harness providers sessions (by hand)",
                }
            );
            var sessions = await manager.ListAsync(false, deadline.Token);
            return await (
                verb switch
                {
                    "list" => ListAsync(sessions, json),
                    "stop" when id.Length > 0 => StopIdAsync(manager, sessions, id, deadline.Token),
                    "start" or "restart" or "stop" => ChangeAsync(manager, sessions, verb, deadline.Token),
                    _ => throw new InvalidOperationException("unknown session operation"),
                }
            );
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidDataException
                        or InvalidOperationException
                        or TimeoutException
                        or OperationCanceledException
                        or System.ComponentModel.Win32Exception
                        or JsonException
            )
        {
            await error.Writer.WriteLineAsync(
                "harness providers sessions" + (verb is "list" ? "" : " " + verb) + ": " + exception.Message
            );
            return 1;
        }
    }

    private async Task<int> ListAsync(IReadOnlyList<GraphifySession> sessions, bool json)
    {
        if (json)
        {
            await output.WriteLineAsync(
                new JsonObject
                {
                    ["sessions"] = new JsonArray(sessions.Select(SessionJson).Cast<JsonNode?>().ToArray()),
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            );
        }
        else if (sessions.Count is 0)
        {
            await output.WriteLineAsync("No graphify sessions.");
        }
        else
        {
            foreach (var session in sessions)
            {
                await output.WriteLineAsync($"{Short(session.Id), -9} {Home(session.Root), -46} {Summary(session)}");
            }
        }
        return 0;
    }

    private async Task<int> StopIdAsync(
        GraphifySessionManager manager,
        IReadOnlyList<GraphifySession> sessions,
        string id,
        CancellationToken token
    )
    {
        var session =
            sessions.FirstOrDefault(s => s.Id.StartsWith(id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("no session " + id);
        await manager.StopAsync(session.Id, token);
        await output.WriteLineAsync($"Stopped {Short(session.Id)} ({Home(session.Root)}).");
        return 0;
    }

    private async Task<int> ChangeAsync(
        GraphifySessionManager manager,
        IReadOnlyList<GraphifySession> sessions,
        string verb,
        CancellationToken token
    )
    {
        var mine = sessions
            .Where(s => s.Reachable && GraphifyDiscovery.PathComparer.Equals(s.Root, manager.Root))
            .ToArray();
        var changed = 0;
        if (verb is "stop" or "restart")
        {
            foreach (var session in mine)
            {
                await manager.StopAsync(session.Id, token);
                changed++;
            }
        }
        if (verb is "start" or "restart" && (verb is "restart" || mine.Length is 0))
        {
            changed = await manager.StartAsync(token) is null ? 0 : 1;
        }
        var message = verb switch
        {
            "stop" => $"Stopped {changed} session(s) of {Home(manager.Root)}.",
            _ when changed is 0 =>
                $"{Home(manager.Root)} already has a session (harness providers sessions restart to start afresh).",
            _ =>
                $"Started a session for {Home(manager.Root)}; it loads in the background (harness providers sessions).",
        };
        await output.WriteLineAsync(message);
        return 0;
    }

    private static JsonObject SessionJson(GraphifySession s)
    {
        JsonObject view = new()
        {
            ["id"] = s.Id,
            ["pid"] = s.Pid,
            ["reachable"] = s.Reachable,
            ["state"] = s.State,
            ["ready"] = s.Ready,
            ["root"] = s.Root,
            ["input"] = s.Input,
            ["memory_bytes"] = s.MemoryBytes,
            ["uptime_ms"] = s.UptimeMs,
        };
        foreach (
            var (name, value) in new[]
            {
                ("stage", s.Stage),
                ("started_by", s.StartedBy),
                ("discovery_incomplete", s.DiscoveryIncomplete),
                ("last_query", s.LastQuery),
            }
        )
        {
            if (value.Length > 0)
            {
                view[name] = value;
            }
        }
        if (s.LoadTookMs is not 0)
        {
            view["load_took_ms"] = s.LoadTookMs;
        }
        return view;
    }

    private static string Summary(GraphifySession s)
    {
        List<string> parts =
        [
            s switch
            {
                { Reachable: false } => "not reachable (process gone)",
                { Ready: true } => "ready",
                { Stage.Length: > 0 } => "loading (" + s.Stage + ")",
                _ => "loading",
            },
        ];
        if (s.Remaining() is { TotalSeconds: > 0 } remaining)
        {
            parts[0] += ", about " + Duration((long)Math.Floor(remaining.TotalSeconds + .5)) + " left";
        }
        if (s.MemoryBytes > 0)
        {
            parts.Add((s.MemoryBytes / (double)(1L << 30)).ToString("F1", CultureInfo.InvariantCulture) + " GB");
        }
        if (s.UptimeMs > 0)
        {
            parts.Add("up " + Age(TimeSpan.FromMilliseconds(s.UptimeMs)));
        }
        if (DateTimeOffset.TryParse(s.LastQuery, CultureInfo.InvariantCulture, out var at))
        {
            parts.Add("queried " + Age(DateTimeOffset.UtcNow - at) + " ago");
        }
        if (s.DiscoveryIncomplete.Length > 0)
        {
            parts.Add("discovery incomplete (" + s.DiscoveryIncomplete + "): queries run slow");
        }
        parts.Add(s.StartedBy.Length > 0 ? "started by " + s.StartedBy : "started outside harness");
        return string.Join(", ", parts);
    }

    private static string Duration(long seconds) =>
        seconds switch
        {
            >= 3600 => string.Create(
                CultureInfo.InvariantCulture,
                $"{seconds / 3600}h{seconds / 60 % 60}m{seconds % 60}s"
            ),
            >= 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m{seconds % 60}s"),
            _ => seconds.ToString(CultureInfo.InvariantCulture) + "s",
        };

    private static string Age(TimeSpan age) =>
        age.TotalHours switch
        {
            >= 48 => ((long)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d",
            >= 1 => ((long)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h",
            _ when age.TotalMinutes >= 1 => ((long)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m",
            _ => ((long)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s",
        };

    private static string Short(string id) => id[..Math.Min(8, id.Length)];

    private static string Home(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }
}
