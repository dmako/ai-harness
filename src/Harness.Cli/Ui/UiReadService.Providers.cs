using System.Text.Json.Nodes;
using Harness.Core.Graphify;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using Harness.Core.Web;
using Harness.Infrastructure.Architecture;
using Harness.Infrastructure.Clients;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Hooks;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Sync;
using Harness.Infrastructure.Telemetry;
using Harness.Providers.Graphify;
using Harness.Providers.Packs;
using Harness.Providers.Registry;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    private async Task<JsonNode?> HealthAsync(JsonObject project, UiReadRequest request, CancellationToken token)
    {
        RequirePresent(project);
        var probe = request.Parameter("probe") is "1";
        if (probe)
        {
            Trusted(request, "probing providers");
        }
        var (inspection, checks) = await ChecksAsync(project, token);
        if (probe)
        {
            await using var providers = ConfiguredProviders.Build(
                inspection.Location.Configuration.Value.Providers,
                inspection.Location.Root,
                new ProviderProcessFactory()
            );
            foreach (var provider in providers.Providers())
            {
                var health = await provider.HealthAsync(true, token);
                checks.RemoveAll(c => c.Name == provider.Name);
                checks.Add(
                    new(
                        provider.Name,
                        health.State switch
                        {
                            "ready" or "idle" => "ok",
                            "degraded" => "warn",
                            _ => "fail",
                        },
                        health.Detail ?? health.State,
                        health.Fix
                    )
                );
            }
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["status"] = HealthStatus(project, inspection, token),
                ["checks"] = checks,
                ["healthy"] = !checks.Any(c => c.Level is "fail"),
                ["probed"] = probe,
                ["fixable"] = checks.Where(HarnessFix.Fixable).Select(c => c.Name).ToArray(),
            }
        );
    }

    /// <summary>The project's checks without probing its providers, as Health shows them first.</summary>
    private async Task<(ProjectInspection Inspection, List<HealthCheck> Checks)> ChecksAsync(
        JsonObject project,
        CancellationToken token
    )
    {
        var inspection = await lifecycle.InspectAsync(UiProjectCatalog.Text(project, "root"), token);
        var checks = inspection.Checks(lifecycle.Version).ToList();
        checks.AddRange(ClientChecks(inspection.Location.Root));
        foreach (var (name, provider) in inspection.Location.Configuration.Value.Providers)
        {
            checks.Add(
                new(name, "ok", provider.IsEnabled() ? "configured: " + provider.Command + " (not probed)" : "disabled")
            );
        }
        return (inspection, checks);
    }

    internal static IReadOnlyList<HealthCheck> ClientChecks(string root)
    {
        var clients = new ClientConfiguration(ClientPaths.Current());
        return clients
            .All.Select(client =>
            {
                var status = clients.Detect(client, root);
                if (status.Errors.Count > 0)
                {
                    return new HealthCheck(
                        client.Id,
                        "fail",
                        "unreadable config: " + status.Errors[0],
                        "fix the syntax in that file; harness will not modify a malformed client config"
                    );
                }
                if (!status.Configured)
                {
                    return new HealthCheck(
                        client.Id,
                        "warn",
                        "harness MCP server not configured (checked " + string.Join(", ", status.Checked) + ")",
                        "harness install --client " + client.Id + " (or see: harness docs connecting-claude-code-and-codex)"
                    );
                }
                if (status.Entries.FirstOrDefault(e => e.Problem.Length > 0) is { } broken)
                {
                    return new HealthCheck(
                        client.Id,
                        "fail",
                        broken.Scope + " scope in " + broken.ConfigPath + ": " + broken.Problem,
                        "correct the command path in " + broken.ConfigPath
                    );
                }
                return new HealthCheck(
                    client.Id,
                    "ok",
                    string.Join(
                        "; ",
                        status.Entries.Select(e => e.Scope + " scope: " + e.Command + " " + string.Join(' ', e.Args))
                    )
                );
            })
            .ToArray();
    }

    private static JsonObject HealthStatus(JsonObject project, ProjectInspection inspection, CancellationToken token)
    {
        var status = UiJson.Node(inspection.Status())!.AsObject();
        var clients = new ClientConfiguration(ClientPaths.Current());
        status["clients"] = UiJson.Node(
            clients
                .All.Select(client =>
                {
                    var detected = clients.Detect(client, inspection.Location.Root);
                    return new JsonObjectMap
                    {
                        ["client"] = client.Id,
                        ["configured"] = detected.Configured,
                        ["scopes"] = detected.Entries.Count is 0
                            ? null
                            : detected.Entries.Select(e => e.Scope).ToArray(),
                    };
                })
                .ToArray()
        );
        foreach (
            var section in new[]
            {
                status,
                status["project"] as JsonObject,
                status["config"] as JsonObject,
                status["database"] as JsonObject,
            }.OfType<JsonObject>()
        )
        {
            foreach (var key in section.Where(p => p.Value is null).Select(p => p.Key).ToArray())
            {
                section.Remove(key);
            }
        }
        if (project["live"]?["state"]?.GetValue<string>() is not "ok")
        {
            return status;
        }
        using var database = Open(project);
        var worktree = inspection.Worktrees.FirstOrDefault(w => w.Path == inspection.Location.Root);
        if (worktree is null)
        {
            return status;
        }
        var state = SyncStore.LoadState(database, worktree.Id);
        JsonObject sync = new() { ["runs"] = UiJson.Node(SyncStore.RecentRuns(database, worktree.Id, 10)) };
        var stale = project["live"]?["stale"]?.GetValue<string>() ?? "";
        if (stale.Length > 0)
        {
            sync["stale"] = stale;
        }
        if (state is not null)
        {
            sync["branch"] = state.Branch;
            sync["commit"] = state.Commit;
            sync["synced_at"] = UiJson.Node(state.SyncedAt);
        }
        token.ThrowIfCancellationRequested();
        status["sync"] = sync;
        return status;
    }

    private static void RequirePresent(JsonObject project)
    {
        if (project["live"]?["state"]?.GetValue<string>() is "missing")
        {
            throw new UiRequestException(409, project["live"]?["detail"]?.GetValue<string>() ?? "repository not found");
        }
    }

    private async Task<JsonNode?> ArchitectureAsync(JsonObject project, UiReadRequest request, CancellationToken token)
    {
        Trusted(request, "loading the architecture starts providers");
        RequirePresent(project);
        var root = UiProjectCatalog.Text(project, "root");
        var location = await lifecycle.LocateAsync(root, cancellationToken: token);
        await using var registry = ConfiguredProviders.Build(
            location.Configuration.Value.Providers,
            root,
            new ProviderProcessFactory()
        );
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await registry.DiscoverAsync(deadline.Token);
        var indexed = registry.IndexedProviders();
        var stale = project["live"]?["stale"]?.GetValue<string>() ?? "";
        List<string> withheld = [];
        var result = await registry.OverviewAsync(
            name =>
            {
                if (stale.Contains("branch", StringComparison.Ordinal) && indexed.Contains(name))
                {
                    withheld.Add(name);
                    return true;
                }
                return false;
            },
            deadline.Token
        );
        return UiJson.Node(
            new JsonObjectMap
            {
                ["structure"] = result.Structure,
                ["errors"] = result.Errors,
                ["withheld"] = withheld.Count is 0 ? null : withheld,
                ["stale"] = stale,
            }
        );
    }

    private async Task<JsonNode?> CouplingAsync(JsonObject project, UiReadRequest request, CancellationToken token)
    {
        Trusted(request, "change coupling starts providers");
        RequirePresent(project);
        var root = UiProjectCatalog.Text(project, "root");
        var location = await lifecycle.LocateAsync(root, cancellationToken: token);
        await using var registry = ConfiguredProviders.Build(
            location.Configuration.Value.Providers,
            root,
            new ProviderProcessFactory()
        );
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await registry.DiscoverAsync(deadline.Token);
        var repository = await GitRepository.DiscoverAsync(root, deadline.Token);
        var hotspots = await ChangeCoupling.HotspotsAsync(repository, registry, 10, 3, deadline.Token);
        var result = UiJson
            .Node(
                new JsonObjectMap
                {
                    ["hotspots"] = hotspots.Hotspots,
                    ["commits"] = hotspots.Commits,
                    ["window_days"] = 180,
                    ["providers"] = registry.For(Capabilities.ArchChangeCoupling).Count,
                    ["errors"] = hotspots.Errors,
                }
            )!
            .AsObject();
        var changed = await repository.DirtyPathsAsync(deadline.Token);
        if (changed.Count > 0)
        {
            var check = await ChangeCoupling.CheckAsync(registry, root, changed, deadline.Token);
            result["changed"] = UiJson.Node(changed);
            result["missing"] = UiJson.Node(check.Missing);
            result["errors"] = UiJson.Node(hotspots.Errors.Concat(check.Errors).ToArray());
        }
        return result;
    }

    private async Task<JsonNode?> ProvidersAsync(
        JsonObject project,
        HarnessDatabase database,
        UiReadRequest request,
        CancellationToken token
    )
    {
        var root = UiProjectCatalog.Text(project, "root");
        var location = await lifecycle.LocateAsync(root, cancellationToken: token);
        var configuration = location.Configuration.Value;
        var since = DateTimeOffset.UtcNow.AddDays(-Days(request));
        var store = new TelemetryStore(database);
        var metrics = store.Metrics(since);
        var trends = store.Trends(since, DateTimeOffset.UtcNow);
        var languages = ProviderRouting.DetectLanguages(root);
        List<JsonObject> providers = [];
        foreach (var (name, configured) in configuration.Providers.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            JsonObject view = new()
            {
                ["name"] = name,
                ["type"] = configured.Type,
                ["command"] = configured.Command,
                ["enabled"] = configured.IsEnabled(),
            };
            void Optional(string key, string value)
            {
                if (value.Length > 0)
                {
                    view[key] = value;
                }
            }
            Optional("pack", configured.Pack);
            Optional(
                "inactive",
                configured.IsEnabled() ? ProviderRouting.Inactive(configured.Languages, languages) : "disabled"
            );
            if (configured.Languages.Length > 0)
            {
                view["languages"] = UiJson.Node(configured.Languages);
            }
            if (configured.Only.Length > 0)
            {
                view["only"] = UiJson.Node(configured.Only);
            }
            var declared = configured.Capabilities.ToDictionary(p => p.Key, p => p.Value.Tool, StringComparer.Ordinal);
            foreach (var capability in configured.Only.Where(_ => configured.Type is not "mcp"))
            {
                declared.TryAdd(capability, "");
            }
            var observed = metrics
                .Providers.Where(p => p.Provider == name)
                .ToDictionary(p => p.Capability, StringComparer.Ordinal);
            var capabilities = declared
                .Keys.Where(c => configured.Only.Length is 0 || configured.Only.Contains(c, StringComparer.Ordinal))
                .Concat(observed.Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(capability =>
                {
                    JsonObject item = new()
                    {
                        ["capability"] = capability,
                        ["declared"] = declared.ContainsKey(capability),
                    };
                    if (declared.GetValueOrDefault(capability) is { Length: > 0 } tool)
                    {
                        item["tool"] = tool;
                    }
                    if (observed.TryGetValue(capability, out var row))
                    {
                        item["metrics"] = UiJson.Node(row);
                    }
                    if (trends.FirstOrDefault(t => t.Provider == name && t.Capability == capability) is { } trend)
                    {
                        item["trend"] = UiJson.Node(trend);
                    }
                    return item;
                })
                .ToArray();
            view["capabilities"] = capabilities.Length is 0 ? null : UiJson.Node(capabilities);
            providers.Add(view);
        }
        return UiJson.Node(
            new JsonObjectMap
            {
                ["languages"] = languages,
                ["providers"] = providers.Count is 0 ? null : providers,
                ["architecture"] = ArchitectureProviders.Coverage(configuration),
            }
        );
    }

    private static async Task<JsonNode?> HooksAsync(
        JsonObject project,
        HarnessDatabase database,
        CancellationToken token
    )
    {
        var root = UiProjectCatalog.Text(project, "root");
        JsonObject result = new() { ["git"] = new JsonArray(), ["clients"] = new JsonArray() };
        try
        {
            var hooks = await GitHooks.ForAsync(await GitRepository.DiscoverAsync(root, token), token);
            result["git_dir"] = hooks.DirectoryPath;
            result["git"] = UiJson.Node(
                hooks
                    .Status()
                    .Select(c => new JsonObjectMap
                    {
                        ["name"] = Path.GetFileName(c.Path),
                        ["installed"] = c.Action is "unchanged",
                    })
                    .ToArray()
            );
        }
        catch (Exception exception) when (ReadError(exception))
        {
            result["git_error"] = exception.Message;
        }
        var installed = false;
        foreach (var client in new[] { "claude-code", "codex" })
        {
            foreach (var target in ClientHookCleanup.Targets(ClientPaths.Current(), client, "all", root))
            {
                JsonObject item = new()
                {
                    ["client"] = client,
                    ["scope"] = target.Scope,
                    ["path"] = target.Path,
                };
                try
                {
                    var command = ClientHookCleanup.Installed(target.Path);
                    installed |= command.Length > 0;
                    if (command.Length > 0)
                    {
                        item["command"] = command;
                    }
                    if (ClientHookCleanup.CommandProblem(command) is { Length: > 0 } problem)
                    {
                        item["problem"] = problem;
                    }
                    if (
                        client is "claude-code"
                        && ClientHookCleanup.Installed(target.Path, "session") is { Length: > 0 } session
                    )
                    {
                        item["session"] = session;
                    }
                }
                catch (Exception exception) when (ReadError(exception))
                {
                    item["error"] = exception.Message;
                }
                result["clients"]!.AsArray().Add((JsonNode)item);
            }
        }
        var events = new TelemetryStore(database).Metrics(DateTimeOffset.UtcNow.AddDays(-7)).Hooks;
        result["events"] = UiJson.Node(events);
        if (installed && events.Events is 0)
        {
            result["note"] =
                "A client hook is installed but recorded no event in 7 days: it may not run. Start an agent session with HARNESS_HOOK_DEBUG=1 in its environment and look at the hook's output (Claude Code: verbose mode, ctrl+o): no output means the command does not run, a message says why the event was dropped.";
        }
        return result;
    }

    private async Task<JsonNode?> GraphifySessionsAsync(UiReadRequest request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var listed = await sessions.ListAsync(deadline.Token);
            var projects = await catalog.ListAsync(token);
            List<JsonObject> result = [];
            foreach (var session in listed)
            {
                var project = projects.FirstOrDefault(p =>
                    GraphifyDiscovery.PathComparer.Equals(
                        GraphifyDiscovery.Canonical(UiProjectCatalog.Text(p, "root")),
                        session.Root
                    )
                );
                var id = project is null ? "" : UiProjectCatalog.Text(project, "id");
                if (request.Parameter("project") is { Length: > 0 } only && only != id)
                {
                    continue;
                }
                var view = UiJson.Node(session)!.AsObject();
                view["summary"] = GraphifyProvider.Summary(session);
                if (project is not null)
                {
                    view["project_id"] = id;
                    view["project_name"] = UiProjectCatalog.Text(project, "name");
                }
                result.Add(view);
            }
            return UiJson.Node(new JsonObjectMap { ["sessions"] = result });
        }
        catch (Exception exception)
            when (ReadError(exception)
                || exception is TimeoutException
                || exception is OperationCanceledException && !token.IsCancellationRequested
            )
        {
            return UiJson.Node(
                new JsonObjectMap { ["sessions"] = Array.Empty<object>(), ["error"] = exception.Message }
            );
        }
    }
}
