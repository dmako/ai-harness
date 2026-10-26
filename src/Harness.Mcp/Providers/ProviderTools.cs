using System.Text.Json;
using Harness.Core.Providers;
using Harness.Core.Serialization;
using Harness.Mcp.Workspaces;

namespace Harness.Mcp.Providers;

public sealed class ProviderTools(
    IProviderRegistry? registry = null,
    IProviderProjectSession? project = null,
    WorkspaceTools? workspace = null
)
{
    public static bool Handles(string name) =>
        name is "search_project" or "provider_status" or "package_api" or "project_overview";

    public Task<object> CallAsync(
        string name,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken
    ) =>
        name switch
        {
            "search_project" => SearchAsync(arguments, cancellationToken),
            "provider_status" => StatusAsync(cancellationToken),
            "package_api" => PackageAsync(arguments, cancellationToken),
            "project_overview" => OverviewAsync(cancellationToken),
            _ => throw new ArgumentException("unknown provider tool " + name, nameof(name)),
        };

    /// <summary>
    /// A validated search_project request: what to search for, with which capabilities, under which path.
    /// </summary>
    private sealed record SearchTarget(string Query, string Kind, string[] Capabilities, string Path)
    {
        public static SearchTarget From(IDictionary<string, JsonElement>? arguments)
        {
            var query = String(arguments, "query").Trim();
            if (query.Length is 0)
            {
                throw new ArgumentException("query must not be empty");
            }

            var kind = String(arguments, "kind") is { Length: > 0 } given ? given : "all";
            var capabilities = KindCapabilities(kind);
            var path = String(arguments, "path");
            if (path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal))
            {
                throw new ArgumentException("path must be relative to the project root");
            }

            return new(query, kind, capabilities, path);
        }

        private static string[] KindCapabilities(string kind) =>
            kind switch
            {
                "all" => [Core.Providers.Capabilities.SymbolSearch, Core.Providers.Capabilities.TextSearch],
                "symbol" => [Core.Providers.Capabilities.SymbolSearch],
                "text" => [Core.Providers.Capabilities.TextSearch],
                "file" => [Core.Providers.Capabilities.FileSearch],
                "callers" => [Core.Providers.Capabilities.Callers],
                "references" => [Core.Providers.Capabilities.References],
                "impact" => [Core.Providers.Capabilities.Impact],
                _ => throw new ArgumentException(
                    "kind must be one of: symbol, text, file, callers, references, impact, all"
                ),
            };
    }

    /// <summary>
    /// What the providers answered to one search: its items (except withheld indexed ones), errors,
    /// not-indexed notes and, when debugging, each call.
    /// </summary>
    private sealed class SearchResults(bool debug, IReadOnlySet<string> indexed)
    {
        private readonly List<object> _items = [];
        private readonly List<object> _calls = [];
        private readonly List<string> _errors = [];
        private readonly List<string> _notIndexed = [];
        private int _asked;
        private bool _truncated;

        public void Record(ProviderResult result)
        {
            _asked++;
            var call = new Dictionary<string, object?>
            {
                ["provider"] = result.Provider,
                ["capability"] = result.Capability,
                ["items"] = result.Items.Count,
                ["latency_ms"] = (long)result.Latency.TotalMilliseconds,
                ["bytes"] = result.Bytes,
            };
            if (result.Truncated)
            {
                call["truncated"] = true;
            }

            if (result.Error is { } error)
            {
                call["error"] = error.Message;
                var destination = error is ProviderNotIndexedException ? _notIndexed : _errors;
                var line = FirstLine(error.Message);
                if (!destination.Contains(line, StringComparer.Ordinal))
                {
                    destination.Add(line);
                }
            }
            _truncated |= result.Truncated;
            if (debug)
            {
                _calls.Add(call);
            }

            if (!indexed.Contains(result.Provider))
            {
                _items.AddRange(result.Items.Select(item => SearchItem(item, debug)));
            }
        }

        /// <summary>Adds the results, errors, truncation, calls and note to the answer.</summary>
        public void WriteTo(Dictionary<string, object?> output, string kind)
        {
            output["results"] = _items;
            if (_errors.Count > 0)
            {
                output["errors"] = _errors;
            }

            if (_truncated)
            {
                output["truncated"] = true;
            }

            if (_calls.Count > 0)
            {
                output["calls"] = _calls;
            }

            Add(output, "note", Note(kind));
        }

        /// <summary>No provider for the kind, the not-indexed providers, and a hint when nothing was found.</summary>
        private string Note(string kind)
        {
            var note = _asked is 0
                ? "no provider offers this kind of search; configure providers (see provider_status)"
                : "";
            if (_notIndexed.Count > 0)
            {
                note = string.Join("; ", _notIndexed.Append(note)).TrimEnd(';', ' ');
            }

            if (_items.Count is 0 && _asked > 0)
            {
                note = (note + "; " + EmptyHint(kind, _errors.Count > 0)).TrimStart(';', ' ');
            }

            return note;
        }

        private static Dictionary<string, object?> SearchItem(ProviderItem item, bool debug)
        {
            var result = new Dictionary<string, object?> { ["kind"] = item.Kind, ["provider"] = item.Provider };
            Add(result, "name", item.Name);
            Add(result, "label", item.Label);
            Add(result, "path", item.Path);
            Add(result, "snippet", item.Snippet);
            if (item.StartLine is not 0)
            {
                result["start_line"] = item.StartLine;
            }

            if (item.EndLine is not 0)
            {
                result["end_line"] = item.EndLine;
            }

            if (item.Depth is not 0)
            {
                result["depth"] = item.Depth;
            }

            if (debug)
            {
                Add(result, "qualified_name", item.QualifiedName);
                Add(result, "tool", item.Tool);
                if (item.Score is not 0)
                {
                    result["score"] = item.Score;
                }
            }
            return result;
        }

        private static string EmptyHint(string kind, bool failed) =>
            (failed, kind) switch
            {
                (true, _) =>
                    "nothing answered: the providers above failed; try kind text, or the provider's own tool, and keep using harness for the next question",
                (_, "callers" or "references" or "impact") =>
                    "none found: provider graphs can miss uses through interfaces, generics, dependency injection or reflection, or lag new code; try kind text for the name before concluding there are none",
                (_, "symbol") => "no symbol found: try kind text (names in strings or config) or part of the name",
                _ => "nothing found: try another wording, part of a name, or kind text",
            };
    }

    private async Task<object> SearchAsync(IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var target = SearchTarget.From(arguments);
        var scope = String(arguments, "scope");
        if (scope is not ("" or "project" or "workspace"))
        {
            throw new ArgumentException("scope must be project or workspace");
        }

        if (scope is "workspace")
        {
            return await WorkspaceSearchAsync(target, arguments, token).ConfigureAwait(false);
        }

        await DiscoverAsync(token).ConfigureAwait(false);
        var debug = Debug(arguments);
        var (snapshot, indexed) = await IndexedSnapshotAsync(token).ConfigureAwait(false);
        var results = new SearchResults(debug, indexed);
        await CollectAsync(target, arguments, results, token).ConfigureAwait(false);
        var output = new Dictionary<string, object?> { ["query"] = target.Query };
        results.WriteTo(output, target.Kind);
        AddStale(output, snapshot, indexed);
        return output;
    }

    private static bool Debug(IDictionary<string, JsonElement>? arguments) =>
        arguments?.TryGetValue("debug", out var value) is true && value.GetBoolean();

    /// <summary>
    /// The project snapshot (read only when a provider is indexed) and, on another branch, the indexed
    /// providers whose results are withheld.
    /// </summary>
    private async Task<(ProviderProjectSnapshot Snapshot, IReadOnlySet<string> Indexed)> IndexedSnapshotAsync(
        CancellationToken token
    )
    {
        var snapshot =
            registry?.IndexedProviders().Count > 0
                ? await SnapshotAsync(token).ConfigureAwait(false)
                : new ProviderProjectSnapshot();
        var indexed = snapshot.CrossBranch
            ? registry?.IndexedProviders() ?? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return (snapshot, indexed);
    }

    /// <summary>Asks the providers of each of the target's capabilities, in order.</summary>
    private async Task CollectAsync(
        SearchTarget target,
        IDictionary<string, JsonElement>? arguments,
        SearchResults results,
        CancellationToken token
    )
    {
        foreach (var capability in target.Capabilities)
        {
            foreach (
                var result in await CallAsync(new(capability, target.Query, target.Path, Limit(arguments)), token)
                    .ConfigureAwait(false)
            )
            {
                results.Record(result);
            }
        }
    }

    private async Task<object> WorkspaceSearchAsync(
        SearchTarget target,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken token
    )
    {
        var depth = String(arguments, "depth");
        if (depth is not ("" or "light" or "deep"))
        {
            throw new ArgumentException("depth must be light or deep");
        }

        if (workspace is null)
        {
            throw new InvalidOperationException(
                "this project is not in a workspace; use 'harness workspace create' to add it"
            );
        }

        return await workspace
            .SearchAsync(
                target.Query,
                target.Kind,
                target.Capabilities,
                target.Path,
                Limit(arguments),
                depth is "deep",
                Debug(arguments),
                token
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The index's staleness (naming the withheld indexed providers on another branch), when any is indexed.
    /// </summary>
    private void AddStale(
        Dictionary<string, object?> output,
        ProviderProjectSnapshot snapshot,
        IReadOnlySet<string> indexed
    )
    {
        var stale = snapshot.Stale;
        if (snapshot.CrossBranch && indexed.Count > 0)
        {
            stale +=
                "; results from "
                + string.Join(", ", indexed.Order(StringComparer.Ordinal))
                + " were withheld until then";
        }

        if (registry?.IndexedProviders().Count > 0)
        {
            Add(output, "stale", stale);
        }
    }

    private async Task<object> PackageAsync(IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var action = String(arguments, "action");
        var capability = action switch
        {
            "search" => Capabilities.PackageSearch,
            "info" => Capabilities.PackageInfo,
            "types" => Capabilities.PackageTypes,
            "definition" => Capabilities.PackageTypeDefinition,
            _ => throw new ArgumentException("action must be one of: search, info, types, definition"),
        };
        var query = String(arguments, "query").Trim();
        var package = String(arguments, "package").Trim();
        var type = String(arguments, "type").Trim();
        if (action is "search" && query.Length is 0)
        {
            throw new ArgumentException("search needs query");
        }

        if (action is not "search" && package.Length is 0)
        {
            throw new ArgumentException(action + " needs package");
        }

        if (action is "definition" && type.Length is 0)
        {
            throw new ArgumentException("definition needs type");
        }

        var results = await CallAsync(
                new(
                    capability,
                    query,
                    Limit: Limit(arguments),
                    Package: package,
                    Version: String(arguments, "version").Trim(),
                    Type: type
                ),
                token
            )
            .ConfigureAwait(false);
        var output = new Dictionary<string, object?> { ["action"] = action };
        if (results.Count is 0)
        {
            output["note"] =
                "no provider offers "
                + capability
                + "; configure a package provider such as NugetMcpServer (see: harness docs providers)";
            return output;
        }
        List<object> items = [],
            answers = [];
        List<string> errors = [];
        foreach (var result in results)
        {
            if (result.Error is { } error)
            {
                errors.Add(FirstLine(error.Message));
                continue;
            }
            if (result.Truncated)
            {
                output["truncated"] = true;
            }

            foreach (var item in result.Items)
            {
                var mapped = new Dictionary<string, object?> { ["kind"] = item.Kind, ["provider"] = item.Provider };
                Add(mapped, "name", item.Name);
                Add(mapped, "qualified_name", item.QualifiedName);
                Add(mapped, "label", item.Label);
                Add(mapped, "snippet", item.Snippet);
                items.Add(mapped);
            }
            if (result.Text.Length > 0)
            {
                answers.Add(new JsonObjectMap { ["provider"] = result.Provider, ["text"] = result.Text });
            }
        }
        if (items.Count > 0)
        {
            output["results"] = items;
        }

        if (answers.Count > 0)
        {
            output["answers"] = answers;
        }

        if (errors.Count > 0)
        {
            output["errors"] = errors;
        }

        if (items.Count is 0 && answers.Count is 0 && errors.Count > 0)
        {
            output["note"] =
                "no package provider could answer (errors above): a package from a private feed may not be reachable by the provider. Read its restored copy instead (NuGet: ~/.nuget/packages/<id>/<version>/, with its XML docs), or search_project kind symbol for how this repository uses it";
        }

        return output;
    }

    private async Task<object> OverviewAsync(CancellationToken token)
    {
        await DiscoverAsync(token).ConfigureAwait(false);
        var snapshot = await SnapshotAsync(token).ConfigureAwait(false);
        var output = new Dictionary<string, object?>
        {
            ["project_root"] = snapshot.Root,
            ["capabilities"] = CapabilitiesOf()
                .GroupBy(cap => cap.Capability, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(cap => cap.Provider).ToArray(),
                    StringComparer.Ordinal
                ),
            ["hint"] = "Use search_project to find code; provider_status if something is missing.",
        };
        Add(output, "project_id", snapshot.ProjectId);
        Add(output, "git_branch", snapshot.Branch);
        Add(output, "git_commit", snapshot.Commit);
        if (snapshot.Stack is { } stack)
        {
            output["stack"] = stack;
        }

        if (registry?.IndexedProviders().Count > 0)
        {
            Add(output, "stale", snapshot.Stale);
        }

        if (registry is not null)
        {
            var indexed = registry.IndexedProviders();
            var (structure, errors) = await registry
                .OverviewAsync(name => snapshot.CrossBranch && indexed.Contains(name), token)
                .ConfigureAwait(false);
            if (structure.Count > 0)
            {
                output["structure"] = structure;
            }

            if (errors.Count > 0)
            {
                output["errors"] = errors;
            }
        }
        return output;
    }

    private async Task<object> StatusAsync(CancellationToken token)
    {
        await DiscoverAsync(token).ConfigureAwait(false);
        var providers = registry?.Providers() ?? [];
        // Probe independent providers concurrently; preserve registration order in the response.
        var entries = await Task.WhenAll(
                providers.Select(async provider =>
                {
                    var health = await provider.HealthAsync(true, token).ConfigureAwait(false);
                    var caps = provider.Capabilities();
                    var entry = new Dictionary<string, object?>
                    {
                        ["name"] = provider.Name,
                        ["state"] = health.State,
                        ["capabilities"] = caps.Count is 0 ? null : caps.Select(cap => cap.Capability).ToArray(),
                    };
                    Add(entry, "detail", health.Detail);
                    Add(entry, "fix", health.Fix);
                    Add(entry, "freshness", caps.Count is 0 ? null : caps[^1].Freshness);
                    if (project is not null)
                    {
                        foreach (
                            var (key, value) in await project
                                .ReadProviderStatisticsAsync(provider.Name, token)
                                .ConfigureAwait(false)
                        )
                        {
                            entry[key] = value;
                        }
                    }

                    return entry;
                })
            )
            .ConfigureAwait(false);
        var metrics = project is null ? null : await project.ReadComplexityMetricsAsync(token).ConfigureAwait(false);
        var output = new Dictionary<string, object?>
        {
            ["providers"] = entries,
            ["architecture"] = ArchitectureCoverage(metrics is not null),
        };
        if (metrics is not null)
        {
            var worker = new Dictionary<string, object?> { ["state"] = metrics.State, ["detail"] = metrics.Detail };
            Add(worker, "fix", metrics.Fix);
            Add(worker, "commit", metrics.Commit);
            output["complexity_metrics"] = worker;
        }
        if (providers.Count is 0)
        {
            output["note"] =
                "no providers configured; add [providers.codebase-memory] / [providers.rg-fd] to the harness config (see: harness docs providers)";
        }

        return output;
    }

    private object[] ArchitectureCoverage(bool metricsWorker)
    {
        var offered = CapabilitiesOf();
        return Capabilities
            .Architecture.Select(capability =>
            {
                var providers = offered
                    .Where(cap => cap.Capability == capability)
                    .Select(cap => cap.Provider)
                    .Concat(metricsWorker && capability is Capabilities.ArchComplexity ? ["metrics-worker"] : [])
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var entry = new Dictionary<string, object?> { ["capability"] = capability, ["providers"] = providers };
                if (providers.Length is 0)
                {
                    entry["without"] = Capabilities.Without(capability);
                }

                return (object)entry;
            })
            .ToArray();
    }

    private Task DiscoverAsync(CancellationToken token) => registry?.DiscoverAsync(token) ?? Task.CompletedTask;

    private Task<ProviderProjectSnapshot> SnapshotAsync(CancellationToken token) =>
        project?.ReadProviderProjectAsync(token) ?? Task.FromResult(new ProviderProjectSnapshot());

    private IReadOnlyList<CapabilityInfo> CapabilitiesOf() => registry?.Capabilities() ?? [];

    private Task<IReadOnlyList<ProviderResult>> CallAsync(ProviderRequest request, CancellationToken token) =>
        registry?.CallAsync(request, cancellationToken: token) ?? Task.FromResult<IReadOnlyList<ProviderResult>>([]);

    private static string String(IDictionary<string, JsonElement>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) is true ? value.GetString() ?? "" : "";

    private static int Limit(IDictionary<string, JsonElement>? arguments) =>
        Math.Clamp(
            arguments?.TryGetValue("limit", out var value) is true && value.GetInt32() > 0 ? value.GetInt32() : 10,
            1,
            50
        );

    private static string FirstLine(string value) => value.Split('\n')[0];

    private static void Add(Dictionary<string, object?> output, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            output[key] = value;
        }
    }
}
