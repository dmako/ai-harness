using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Harness.Core.Providers;
using ModelContextProtocol.Protocol;

namespace Harness.Providers.Mcp;

public class GenericMcpProvider : IProvider, IVersionedProvider
{
    private readonly ProviderConfiguration _configuration;
    private readonly string _root;
    private readonly string _canonicalRoot;
    private readonly bool _linked;
    private readonly McpSupervisor _supervisor;
    private readonly Dictionary<string, Plan> _plans;
    private string _project = "";

    public GenericMcpProvider(
        string name,
        ProviderConfiguration configuration,
        string root,
        IProviderProcessFactory factory,
        IReadOnlyDictionary<string, string?>? environment = null,
        McpRestartPolicy? policy = null,
        TimeProvider? timeProvider = null,
        bool? linkedWorktree = null
    )
    {
        Name = name;
        _configuration = configuration;
        _root = Path.GetFullPath(root);
        _canonicalRoot = Canonical(_root);
        _linked = linkedWorktree ?? IsLinked(_root);
        _plans = configuration.Capabilities.ToDictionary(
            pair => pair.Key,
            pair => new Plan(
                new[] { pair.Value }
                    .Concat(pair.Value.Chain)
                    .Select(mapping => new Call(
                        mapping,
                        Pattern(mapping.Pattern),
                        Pattern(mapping.Miss),
                        mapping.Pattern.Length > 0 ? Pattern(mapping.Truncated) : null
                    ))
                    .ToArray(),
                pair.Value.Combine.Length is 0 ? "first" : pair.Value.Combine
            ),
            StringComparer.Ordinal
        );
        _supervisor = new(
            factory,
            new(
                name,
                new(configuration.Command, configuration.Args)
                {
                    WorkingDirectory = configuration.Dir is "project" ? _root : "",
                    Environment = environment ?? new Dictionary<string, string?>(),
                }
            )
            {
                CallTimeout =
                    configuration.TimeoutMs > 0
                        ? TimeSpan.FromMilliseconds(configuration.TimeoutMs)
                        : TimeSpan.FromSeconds(30),
            },
            policy,
            timeProvider
        );
    }

    public string Name { get; }
    public string ServerVersion => _supervisor.ServerVersion;

    public static IProvider Build(
        string name,
        ProviderConfiguration configuration,
        string root,
        IProviderProcessFactory factory,
        IReadOnlyDictionary<string, string?>? environment = null,
        McpRestartPolicy? policy = null,
        TimeProvider? timeProvider = null,
        bool? linkedWorktree = null
    ) =>
        configuration.Refresh is null
            ? new GenericMcpProvider(
                name,
                configuration,
                root,
                factory,
                environment,
                policy,
                timeProvider,
                linkedWorktree
            )
            : new Refreshing(name, configuration, root, factory, environment, policy, timeProvider, linkedWorktree);

    public IReadOnlyList<CapabilityInfo> Capabilities() =>
        _plans
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new CapabilityInfo(
                pair.Key,
                Name,
                "medium",
                "medium",
                _configuration.Freshness is "indexed" ? "indexed" : "live",
                Core.Providers.Capabilities.IsPackage(pair.Key) ? "external" : "project",
                pair.Value.Calls[0].Mapping.Evidence
            ))
            .ToArray();

    public async ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default)
    {
        if (!probe)
        {
            return _supervisor.Health();
        }

        try
        {
            var client = await _supervisor.ClientAsync(cancellationToken).ConfigureAwait(false);
            var tools = (await client.ListToolsAsync(cancellationToken).ConfigureAwait(false))
                .Select(tool => tool.Name)
                .ToHashSet(StringComparer.Ordinal);
            var missing = _plans
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(pair =>
                    pair.Value.Calls.Where(call => !tools.Contains(call.Mapping.Tool))
                        .Select(call => pair.Key + " -> " + call.Mapping.Tool)
                )
                .ToArray();
            if (missing.Length > 0)
            {
                return new(
                    "degraded",
                    "mapped tools not offered by the server: " + string.Join(", ", missing),
                    "correct providers." + Name + ".capabilities.<capability>.tool in the harness config"
                );
            }

            var health = _supervisor.Health();
            var detail =
                health.Detail
                + string.Create(CultureInfo.InvariantCulture, $"; {_plans.Count} capability mapping(s) verified");
            if (_configuration.Resolve is not null)
            {
                detail += "; project " + await ResolveProjectAsync(cancellationToken).ConfigureAwait(false);
            }

            return health with
            {
                Detail = detail,
            };
        }
        catch (ProviderNotIndexedException error)
        {
            return new("degraded", error.Message, (error.InnerException as ProviderException)?.Hint);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(
                "unavailable",
                CredentialRedactor.Redact(error.Message),
                "check that the configured command starts on its own and speaks MCP over stdio"
            );
        }
    }

    public async Task<ProviderResult> ExecuteAsync(
        ProviderRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (!_plans.TryGetValue(request.Capability, out var plan))
        {
            throw new UnsupportedCapabilityException();
        }

        var limit = request.Limit > 0 ? request.Limit : 20;
        var values = Values(request, limit);
        if (_configuration.Resolve is not null)
        {
            values["project"] = await ResolveProjectAsync(cancellationToken).ConfigureAwait(false);
        }

        var emptyOnMiss = EmptyOnMiss(request.Capability);
        var answers = new Answers();
        foreach (var call in plan.Calls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = await ExecuteCallAsync(request, call, values, limit, cancellationToken).ConfigureAwait(false);
            if (answers.Add(part, call, plan.Calls.Count > 1, emptyOnMiss) is { } failed)
            {
                return failed;
            }

            if (part.Result.Error is null && plan.Combine is "first")
            {
                break;
            }
        }
        var output = answers.Output;
        if (answers.Unanswered(emptyOnMiss))
        {
            output = output with { Error = Failure("", "no answer: " + string.Join("; ", answers.Misses)) };
        }

        return output with
        {
            Items = answers.Items.Take(limit).ToArray(),
            Truncated = output.Truncated || answers.Items.Count > limit,
        };
    }

    /// <summary>Code and architecture questions answer a miss with no items instead of an error.</summary>
    private static bool EmptyOnMiss(string capability) =>
        capability.StartsWith("code.", StringComparison.Ordinal)
        || Core.Providers.Capabilities.IsArchitecture(capability);

    /// <summary>The plan's calls combined: their items, text and summary, and the misses of a chain.</summary>
    private sealed class Answers
    {
        private bool _answered;
        private bool _allMatched = true;
        internal ProviderResult Output { get; private set; } = new();
        internal List<ProviderItem> Items { get; } = [];
        internal List<string> Misses { get; } = [];

        /// <summary>
        /// Adds a call's part; a failed call of a chain is a miss, while a single call's failure is the result
        /// (without an error when it matched the miss pattern and an empty answer is fine).
        /// </summary>
        internal ProviderResult? Add(Part part, Call call, bool chain, bool emptyOnMiss)
        {
            Output = Output with
            {
                Bytes = checked(Output.Bytes + part.Result.Bytes),
                TimedOut = Output.TimedOut || part.Result.TimedOut,
            };
            if (part.Miss)
            {
                _allMatched &= part.Matched;
            }

            if (part.Result.Error is not { } error)
            {
                Answer(part.Result);
                return null;
            }

            if (chain)
            {
                Misses.Add(call.Mapping.Tool + ": " + McpMapping.FirstLine(ErrorMessage(error)));
                return null;
            }
            return Output with { Error = part.Miss && part.Matched && emptyOnMiss ? null : error };
        }

        /// <summary>No call answered, unless every call missed by pattern and an empty answer is fine.</summary>
        internal bool Unanswered(bool emptyOnMiss) => !_answered && !(emptyOnMiss && _allMatched && Misses.Count > 0);

        private void Answer(ProviderResult result)
        {
            _answered = true;
            Items.AddRange(result.Items);
            Output = Output with
            {
                Truncated = Output.Truncated || result.Truncated,
                TimedOut = Output.TimedOut || result.TimedOut,
                Text = Output.Text + (Output.Text.Length > 0 && result.Text.Length > 0 ? "\n\n" : "") + result.Text,
                Summary = Output.Summary ?? result.Summary,
            };
        }

        private static string ErrorMessage(Exception error) =>
            error is ProviderException provider ? provider.Detail : error.Message;
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task<Part> ExecuteCallAsync(
        ProviderRequest request,
        Call call,
        IReadOnlyDictionary<string, string> values,
        int limit,
        CancellationToken cancellationToken
    )
    {
        var mapping = call.Mapping;
        CallToolResult result;
        try
        {
            result = await _supervisor
                .CallToolAsync(mapping.Tool, McpMapping.Arguments(mapping.Args, values), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return CallFailed(call, error);
        }
        var text = McpMapping.Text(result);
        var output = new ProviderResult { Bytes = Encoding.UTF8.GetByteCount(text) };
        if (result.IsError is true)
        {
            return new(
                output with
                {
                    Error = McpMapping.ToolError(Name, mapping.Tool, result),
                },
                true,
                call.Miss?.IsMatch(text) is true
            );
        }

        if (call.Miss?.IsMatch(text) is true)
        {
            return new(output with { Error = Failure(mapping.Tool, McpMapping.FirstLine(text)) }, true, true);
        }

        if (
            request.Capability
            is Core.Providers.Capabilities.PackageInfo
                or Core.Providers.Capabilities.PackageTypeDefinition
        )
        {
            return new(output with { Text = text });
        }

        try
        {
            if (call.Extraction is { } pattern)
            {
                var entries = McpMapping.PatternEntries(pattern, text).ToArray();
                var fields = new CapabilityMapping
                {
                    Fields = pattern
                        .GetGroupNames()
                        .Where(name => !int.TryParse(name, out _))
                        .ToDictionary(name => name, StringComparer.Ordinal),
                    Kind = mapping.Kind,
                };
                output = output with { Truncated = call.Truncated?.IsMatch(text) is true };
                return Items(request, call, entries, fields, output, limit);
            }

            return BodyPart(request, call, McpMapping.Body(result), output, limit);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or RegexMatchTimeoutException)
        {
            return new(output with { Error = Failure(mapping.Tool, error.Message, inner: error) });
        }
    }

    /// <summary>A call that threw; it missed when its message matches the miss pattern.</summary>
    private Part CallFailed(Call call, Exception error)
    {
        var matched = call.Miss?.IsMatch(error.Message) is true;
        return new(
            new()
            {
                Error = Failure(call.Mapping.Tool, error.Message, inner: error),
                TimedOut = error is TimeoutException,
            },
            matched,
            matched
        );
    }

    /// <summary>An overview's summary, the body's error field, or the items listed at the mapped path.</summary>
    private Part BodyPart(ProviderRequest request, Call call, JsonElement body, ProviderResult output, int limit)
    {
        var mapping = call.Mapping;
        if (request.Capability is Core.Providers.Capabilities.Overview)
        {
            return new(output with { Summary = Summary(body) });
        }

        var message = mapping.Error.Length > 0 ? McpMapping.String(McpMapping.Lookup(body, mapping.Error)) : "";
        if (message.Length > 0)
        {
            return new(output with { Error = Failure(mapping.Tool, message) });
        }

        output = output with
        {
            Truncated =
                mapping.Truncated.Length > 0
                && McpMapping.Lookup(body, mapping.Truncated).ValueKind is JsonValueKind.True,
        };
        var collected = McpMapping.Collect(body, mapping.Items);
        if (!collected.Found)
        {
            return new(
                output with
                {
                    Error = Failure(
                        mapping.Tool,
                        $"response has no list at \"{mapping.Items}\" (check providers.{Name}.capabilities.{request.Capability}.items)"
                    ),
                }
            );
        }

        return Items(request, call, collected.Entries, mapping, output, limit);
    }

    private static Dictionary<string, JsonElement> Summary(JsonElement body) =>
        body.ValueKind is JsonValueKind.Object
            ? body.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement> { ["result"] = body };

    /// <summary>
    /// The entries as items within the request's path, up to limit (one more marks it truncated); mapped entries
    /// must match the where filter.
    /// </summary>
    private Part Items(
        ProviderRequest request,
        Call call,
        IReadOnlyList<McpMapping.Entry> entries,
        CapabilityMapping fields,
        ProviderResult output,
        int limit
    )
    {
        var items = new List<ProviderItem>();
        foreach (var entry in entries)
        {
            if (call.Extraction is null && !McpMapping.Matches(entry, call.Mapping.Where))
            {
                continue;
            }

            var item = McpMapping.Item(entry, fields, request.Capability, _root) with
            {
                Provider = Name,
                Tool = call.Mapping.Tool,
            };
            if (!request.Covers(item.Path ?? ""))
            {
                continue;
            }

            if (items.Count == limit)
            {
                return new(output with { Items = items, Truncated = true });
            }

            items.Add(item);
        }
        return new(output with { Items = items });
    }

    private Dictionary<string, string> Values(ProviderRequest request, int limit)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = request.Query,
            ["query_name"] = McpMapping.LastSegment(request.Query),
            ["query_regex"] = QuoteMeta(request.Query),
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["limit_more"] = ((long)limit + 1).ToString(CultureInfo.InvariantCulture),
            ["path"] = request.Path,
            ["root"] = _root,
            ["package"] = request.Package,
            ["version"] = request.Version,
            ["type"] = request.Type,
            ["target"] = request.Target,
            ["target_name"] = McpMapping.LastSegment(request.Target),
        };
        if (request.Budget > 0)
        {
            values["budget"] = request.Budget.ToString(CultureInfo.InvariantCulture);
        }

        if (request.Path.Length > 0)
        {
            var absolute = Path.GetFullPath(request.Path, _root);
            var relative = Path.GetRelativePath(_root, absolute);
            if (
                Path.IsPathRooted(relative)
                || relative is ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            )
            {
                throw new ArgumentException(
                    "Provider request path must stay inside the project root.",
                    nameof(request)
                );
            }

            values["abs_path"] = absolute;
        }
        return values;
    }

    private async Task<string> ResolveProjectAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _project);
        if (cached.Length > 0)
        {
            return cached;
        }

        var mapping = _configuration.Resolve!;
        var result = await _supervisor
            .CallToolAsync(
                mapping.Tool,
                McpMapping.Arguments(mapping.Args, new Dictionary<string, string> { ["root"] = _root }),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (result.IsError is true)
        {
            throw McpMapping.ToolError(Name, mapping.Tool, result);
        }

        var entries = McpMapping.Collect(McpMapping.Body(result), mapping.Items).Entries;
        foreach (var entry in entries)
        {
            var root = McpMapping.String(entry.Field(mapping.Match));
            var value = McpMapping.String(entry.Field(mapping.Value));
            if (root.Length > 0 && value.Length > 0 && Canonical(root) == _canonicalRoot)
            {
                Volatile.Write(ref _project, value);
                return value;
            }
        }
        var message = _linked
            ? $"this worktree is not indexed by {Name} ({_root}); live providers answered"
            : $"this repository is not known to {Name} ({_root})";
        var worktreeFix = _configuration.IndexWorktrees
            ? "run 'harness sync' here to index it"
            : $"to index worktrees too, set providers.{Name}.index_worktrees = true and run 'harness sync' here";
        var fix = _linked ? worktreeFix : mapping.Fix.Replace("{root}", _root, StringComparison.Ordinal);
        throw new ProviderNotIndexedException(message, Failure("", message, fix));
    }

    protected async Task<RefreshResult> RefreshCoreAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        if (_configuration.Resolve is not null)
        {
            try
            {
                _ = await ResolveProjectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderNotIndexedException error) when (!(_linked && _configuration.IndexWorktrees))
            {
                return new(true, error.Message);
            }
            catch (ProviderNotIndexedException)
            { /* Explicit worktree indexing is allowed to create its index. */
            }
        }
        var mapping = _configuration.Refresh!;
        var arguments = mapping.Args.ToDictionary(
            pair => pair.Key,
            pair =>
                pair.Value.ValueKind is JsonValueKind.String
                    ? pair.Value.GetString() switch
                    {
                        "{full}" => (object?)request.Full,
                        "{changed}" => request.Changed?.ToArray() ?? [],
                        { } value => value.Replace("{root}", _root, StringComparison.Ordinal),
                        _ => pair.Value.Clone(),
                    }
                    : pair.Value.Clone(),
            StringComparer.Ordinal
        );
        var result = await _supervisor.CallToolAsync(mapping.Tool, arguments, cancellationToken).ConfigureAwait(false);
        var text = McpMapping.Text(result);
        if (Pattern(mapping.Skip)?.IsMatch(text) is true)
        {
            return new(true, McpMapping.FirstLine(text));
        }

        if (result.IsError is true)
        {
            throw McpMapping.ToolError(Name, mapping.Tool, result);
        }

        var detail = McpMapping.FirstLine(text);
        return new(
            false,
            mapping.Tool
                + ": "
                + (Encoding.UTF8.GetByteCount(detail) > 160 ? McpMapping.Utf8Prefix(detail, 157, "...") : detail)
        );
    }

    private ProviderException Failure(string tool, string message, string hint = "", Exception? inner = null) =>
        new(Name, CredentialRedactor.Redact(message), tool, CredentialRedactor.Redact(hint), inner);

    private static Regex? Pattern(string pattern) =>
        pattern.Length is 0
            ? null
            : new(
                pattern.Replace("(?P<", "(?<", StringComparison.Ordinal),
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromSeconds(1)
            );

    private static string QuoteMeta(string text) =>
        string.Concat(
            text.Select(character =>
                "\\.+*?()|[]{}^$".Contains(character, StringComparison.Ordinal)
                    ? "\\" + character
                    : character.ToString()
            )
        );

    private static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return full;
        }
    }

    private static bool IsLinked(string root)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, ".git"))
                .Replace(Path.DirectorySeparatorChar, '/')
                .Contains("/worktrees/", StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal sealed record Call(CapabilityMapping Mapping, Regex? Extraction, Regex? Miss, Regex? Truncated);

    internal sealed record Plan(IReadOnlyList<Call> Calls, string Combine);

    internal sealed record Part(ProviderResult Result, bool Miss = false, bool Matched = false);

    private sealed class Refreshing(
        string name,
        ProviderConfiguration configuration,
        string root,
        IProviderProcessFactory factory,
        IReadOnlyDictionary<string, string?>? environment,
        McpRestartPolicy? policy,
        TimeProvider? timeProvider,
        bool? linkedWorktree
    )
        : GenericMcpProvider(name, configuration, root, factory, environment, policy, timeProvider, linkedWorktree),
            IProviderRefresher
    {
        public Task<RefreshResult> RefreshAsync(
            RefreshRequest request,
            CancellationToken cancellationToken = default
        ) => RefreshCoreAsync(request, cancellationToken);
    }
}
