using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>Day charts (app.js dayChart): at most 60 bars, a bar per day or run of days, red where calls went wrong.</summary>
public static class DayCharts
{
    public static IReadOnlyList<DayBar> Bars(CallTrend? trend)
    {
        var all = trend?.Days ?? [];
        var per = Math.Max(1, (int)Math.Ceiling(all.Count / 60.0));
        var bars = new List<DayBar>();
        for (var i = 0; i < all.Count; i += per)
        {
            var run = all.Skip(i).Take(per).ToArray();
            var calls = run.Sum(d => d.Calls);
            var bad = run.Sum(d => d.Errors + d.Slow) > 0;
            bars.Add(new DayBar(calls, bad ? calls : 0));
        }
        return bars;
    }

    public static string Name(CallTrend? trend) =>
        $"{(trend?.Days ?? []).Sum(d => d.Calls)} calls over {(trend?.Days ?? []).Count} days";
}

/// <summary>A finding (app.js findingCard): its level, area, message, its days or history, and what to do.</summary>
public sealed record FindingItem(ReportFinding Finding)
{
    public string Icon =>
        Finding.Level switch
        {
            "problem" => "st-fail",
            "warn" => "st-warn",
            _ => "level-info",
        };

    public bool IsProblem => Finding.Level is "problem";
    public bool IsWarn => Finding.Level is "warn";
    public IReadOnlyList<DayBar> Bars => DayCharts.Bars(Finding.Trend);
    public bool HasTrend => Finding.Trend is { Days.Count: > 0 };

    public string TrendNote =>
        HasTrend ? $"{Finding.Trend!.Days[0].Day[5..]} → {Finding.Trend.Days[^1].Day[5..]}, red: days with the problem" : "";

    public bool HasHistory => !HasTrend && Finding.History is { Length: > 0 };
    public bool HasAction => Finding.Action is { Length: > 0 };
    public string ActionLine => "→ " + Finding.Action;
}

/// <summary>
/// A project's Agents (app.js "agents"): How harness serves them, or Tools &amp; providers, by the address
/// (#/p/&lt;id&gt;/agents[/tools]).
/// </summary>
public sealed partial class AgentsViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly string _id;

    public AgentsViewModel(UiServices services, string projectId, Route route)
    {
        _services = services;
        _id = projectId;
        Serving = new ServingSection(services, projectId);
        Tools = new ToolsSection(services, projectId);
        Scm = new ScmCardSection(services, projectId);
        Hooks = new HooksSection(services, projectId);
        Graphify = new GraphifySection(services, projectId);
        Show(route);
    }

    public ServingSection Serving { get; }
    public ToolsSection Tools { get; }
    public ScmCardSection Scm { get; }
    public HooksSection Hooks { get; }
    public GraphifySection Graphify { get; }
    public ObservableCollection<TabItem> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsServing))]
    public partial bool ShowsTools { get; private set; }

    public bool ShowsServing => !ShowsTools;

    public void Start()
    {
        foreach (var section in new SectionViewModel[] { Serving, Tools, Scm, Hooks, Graphify })
        {
            section.Follow(_services.Live);
            _ = section.LoadAsync();
        }
    }

    public void Show(Route route)
    {
        ShowsTools = route.Item is "tools";
        Lists.Sync(
            Sections,
            [
                new TabItem("serving", "How harness serves them", Route.ProjectHref(_id, "agents"), 0, !ShowsTools),
                new TabItem("tools", "Tools & providers", Route.ProjectHref(_id, "agents", "tools"), 0, ShowsTools),
            ]
        );
    }

    public void Dispose()
    {
        Serving.Dispose();
        Tools.Dispose();
        Scm.Dispose();
        Hooks.Dispose();
        Graphify.Dispose();
    }
}

/// <summary>
/// How harness serves the project's agents (app.js viewServing): the verdict, the tiles (calls, context hits, read
/// next, knowledge, providers with problems), what to change most severe first, and the problems that stopped.
/// </summary>
public sealed partial class ServingSection(UiServices services, string projectId) : SectionViewModel("", projectId, ["activity", "sync", "knowledge"])
{
    private static readonly Dictionary<string, int> Rank = new(StringComparer.Ordinal) { ["problem"] = 0, ["warn"] = 1, ["info"] = 2 };

    public ObservableCollection<Tile> Tiles { get; } = [];
    public ObservableCollection<FindingItem> Findings { get; } = [];
    public ObservableCollection<FindingItem> Resolved { get; } = [];

    [ObservableProperty]
    public partial string Verdict { get; private set; } = "";

    [ObservableProperty]
    public partial bool VerdictOk { get; private set; }

    [ObservableProperty]
    public partial string FindingsHint { get; private set; } = "";

    public bool HasFindings => Findings.Count > 0;
    public bool HasResolved => Resolved.Count > 0;
    public string ResolvedHead => $"Resolved: {Shell.Plural(Resolved.Count, "problem")} that stopped";

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var days = services.Host.Days;
        var r = await services.Api.ReportAsync(ProjectId!, days, token);
        var context = r.Context;
        var read = context.AfterRead;
        var all = (r.Findings ?? []).Where(f => f.Audience is not "developer").ToArray();
        var stopped = all.Where(f => f.Message.EndsWith(": resolved", StringComparison.Ordinal)).ToArray();
        var current = all.Except(stopped).OrderBy(f => Rank.GetValueOrDefault(f.Level, 2)).ToArray();
        var act = current.Count(f => f.Level is not "info");
        var tools = r.Metrics?.Tools ?? [];
        var calls = tools.Sum(t => t.Calls);
        var clients = tools.SelectMany(t => t.Clients ?? []).Distinct(StringComparer.Ordinal).Select(Agents.Client).ToArray();
        var last = tools.Select(t => t.LastAt).Where(l => l is { Length: > 0 }).Order(StringComparer.Ordinal).LastOrDefault();
        var hooks = r.Metrics?.Hooks?.Events ?? 0;
        VerdictOk = act is 0 && calls > 0;
        Verdict = Judge(act, calls, days);
        Lists.Sync(
            Tiles,
            [
                new Tile(
                    "Agent calls",
                    calls > 0 ? calls.ToString(CultureInfo.InvariantCulture) : "none",
                    calls > 0 ? $"from {string.Join(", ", clients)}; the last {Shell.Ago(last)}" : "connect an agent: harness install, then harness doctor"
                ),
                new Tile(
                    "Context hits",
                    context.Changed > 0 ? Overview.Pct(context.HitRate) : "—",
                    context.Changed > 0
                        ? $"per answer · {context.Hits} of {context.Changed} changed files over {context.Calls} answers"
                        : "scored once agents' changes are committed (harness sync)"
                ),
                new Tile("Read next", read is { Touched: > 0 } ? Overview.Pct(read.HitRate) : "—", ReadNote(read, hooks)),
                new Tile(
                    "Knowledge",
                    $"{r.KnowledgeReads} read",
                    $"{r.KnowledgeWrites} written by agents; {r.Knowledge?.CurrentMemories ?? 0} current memories"
                ),
                // the C# server's findings name no provider: none is grouped as a provider's problem
                new Tile("Providers with problems", "✓ none", "no provider failed or was slow"),
            ]
        );
        FindingsHint = $"What harness finds over the last {r.Days} days that you can act on, most severe first, each with what to do.";
        Lists.Sync(Findings, current.Select(f => new FindingItem(f)));
        Lists.Sync(Resolved, stopped.Select(f => new FindingItem(f)));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasResolved));
        OnPropertyChanged(nameof(ResolvedHead));
    }

    private static string Judge(int act, int calls, int days)
    {
        if (act > 0)
        {
            return $"{Shell.Plural(act, "thing")} to change so harness serves your agents better: see below.";
        }
        return calls > 0 ? "✓ Harness serves your agents; nothing to change." : $"No agent called harness in {days} days.";
    }

    private static string ReadNote(AfterRead? read, int hooks)
    {
        if (read is { Touched: > 0 })
        {
            return $"of {read.Touched} files agents read or edited after an answer, {read.Hits} were in it";
        }
        return hooks > 0 ? "nothing read after an answer yet" : "measured with the client hooks (Tools & providers → Hooks)";
    }
}

/// <summary>A provider used here (app.js viewProviders card): kind, pack, command, its calls, what was not called.</summary>
public sealed partial class ProviderCard(ProviderInfo info, int days, UiServices services, string projectId) : ObservableObject
{
    public ProviderInfo Info { get; } = info;
    public string Kind => Info.Type is "harness" ? "provider protocol" : "MCP";
    public bool HasPack => Info.Pack is { Length: > 0 };
    public string PackTag => "pack " + Info.Pack;
    public bool HasOnly => Info.Only.Count > 0;
    public string OnlyLine => "Limited to " + string.Join(", ", Info.Only.Select(Short));
    public bool IsGraphify => Info.Name is "graphify-csharp";

    private IEnumerable<ProviderCapability> Called => (Info.Capabilities ?? []).Where(c => c.Metrics is not null);

    public string Summary
    {
        get
        {
            var calls = Called.Sum(c => c.Metrics!.Calls);
            if (calls is 0)
            {
                return $"not called in {days} days";
            }
            var errors = Called.Sum(c => c.Metrics!.Calls * c.Metrics.ErrorRate);
            return $"{Shell.Plural(calls, "call")} · {Overview.Pct(errors / calls)} failed";
        }
    }

    public string? Quiet
    {
        get
        {
            var caps = Info.Capabilities ?? [];
            if (caps.Count is 0)
            {
                return Info.Type is "harness" ? "Declares its capabilities when started; none called yet." : "No capabilities.";
            }
            var quiet = caps.Where(c => c.Metrics is null).ToArray();
            return quiet.Length > 0
                ? $"Not called in {days} days: " + string.Join(", ", quiet.Select(c => Short(c.Capability) + (c.Tool is { Length: > 0 } t ? $" ({t})" : "")))
                : null;
        }
    }

    public bool HasQuiet => Quiet is not null;
    public bool HasCheck => Info.Check is { Level: not "ok" };
    public bool CanIndex => (Info.Capabilities ?? []).Any(c => c.Capability is "project.overview");

    [ObservableProperty]
    public partial string? Indexed { get; private set; }

    /// <summary>What the provider has indexed (app.js indexedSection): its project overview, asked on request.</summary>
    [RelayCommand]
    private async Task LoadIndexAsync()
    {
        Indexed = "Asking the provider (starts it; can take a while)…";
        try
        {
            var a = await services.Api.ArchitectureRawAsync(projectId, CancellationToken.None);
            Indexed = a.TryGetProperty("structure", out var structure) && structure.ValueKind is JsonValueKind.Object && structure.TryGetProperty(Info.Name, out var mine)
                ? Counts(mine)
                : "It returned no overview.";
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Indexed = exception.Message;
        }
    }

    /// <summary>The fix for its check, shown first like Health's (it writes the project's configuration).</summary>
    [RelayCommand]
    private async Task FixAsync()
    {
        using var health = new HealthViewModel(services, projectId);
        await health.FixCommand.ExecuteAsync(new CheckItem(Info.Check!.Name, Info.Check.Level, "", [], Info.Check.Fix, true, true, null));
    }

    private static string Short(string capability) => capability.StartsWith("code.", StringComparison.Ordinal) ? capability[5..] : capability;

    /// <summary>The overview's lists of counts by kind, one line each; anything else as its JSON.</summary>
    private static string Counts(JsonElement summary)
    {
        var lines = new List<string>();
        if (summary.ValueKind is JsonValueKind.Object)
        {
            foreach (var list in summary.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.Array))
            {
                var items = list.Value.EnumerateArray()
                    .Where(x => x.ValueKind is JsonValueKind.Object && x.TryGetProperty("count", out var n) && n.ValueKind is JsonValueKind.Number)
                    .Select(x => $"{Label(x)} {x.GetProperty("count").GetInt32().ToString(CultureInfo.InvariantCulture)}")
                    .ToArray();
                if (items.Length > 0)
                {
                    lines.Add($"{list.Name}: {string.Join(", ", items)}");
                }
            }
        }
        return lines.Count > 0 ? string.Join("\n", lines) : summary.GetRawText();
    }

    private static string Label(JsonElement item)
    {
        foreach (var name in new[] { "type", "label", "name" })
        {
            if (item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String)
            {
                return value.GetString()!;
            }
        }
        return "?";
    }
}

/// <summary>A provider not used here: why (disabled, or serving other languages).</summary>
public sealed record IdleProvider(ProviderInfo Info)
{
    public string Kind => Info.Type is "harness" ? "provider protocol" : "MCP";
    public string Reason => !Info.Enabled ? "disabled" : (Info.Inactive ?? "").Replace("not used here: ", "", StringComparison.Ordinal);
    public string Languages => string.Join(", ", Info.Languages);
}

/// <summary>An architecture capability no provider adds yet, and what is missing without one.</summary>
public sealed record CoverageRow(string Capability, string Without);

/// <summary>Tools &amp; providers (app.js viewProviders): the providers used here and not, and the architecture evidence.</summary>
public sealed partial class ToolsSection(UiServices services, string projectId) : SectionViewModel("", projectId, ["sync"])
{
    public ObservableCollection<ProviderCard> Used { get; } = [];
    public ObservableCollection<IdleProvider> Idle { get; } = [];
    public ObservableCollection<CoverageRow> Missing { get; } = [];

    [ObservableProperty]
    public partial string Hint2 { get; private set; } = "";

    [ObservableProperty]
    public partial string? Complete { get; private set; }

    public bool HasUsed => Used.Count > 0;
    public bool HasIdle => Idle.Count > 0;
    public bool HasMissing => Missing.Count > 0;
    public bool HasArchitecture => HasMissing || Complete is not null;
    public string HealthHref => Route.ProjectHref(ProjectId!, "health");

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var days = services.Host.Days;
        var v = await services.Api.ProvidersAsync(ProjectId!, days, token);
        var providers = v.Providers ?? [];
        Hint2 = $"Code providers harness routes agents' questions to, and how they answered over {days} days (project languages: "
            + (v.Languages.Count > 0 ? string.Join(", ", v.Languages) : "none recognized") + ").";
        Used.Clear();
        foreach (var p in providers.Where(p => p.Enabled && p.Inactive is null))
        {
            Used.Add(new ProviderCard(p, days, services, ProjectId!));
        }
        Lists.Sync(Idle, providers.Where(p => !p.Enabled || p.Inactive is not null).Select(p => new IdleProvider(p)));
        static bool Done(ArchitectureCoverage c) => c.Providers.Count > 0 || (c.Without ?? "").Contains("nothing is missing", StringComparison.Ordinal);
        static string Name(string capability) => capability.Replace("arch.", "", StringComparison.Ordinal).Replace('_', ' ');
        Lists.Sync(Missing, v.Architecture.Where(c => !Done(c)).Select(c => new CoverageRow(Name(c.Capability), c.Without ?? "—")));
        var served = v.Architecture.Where(Done).ToArray();
        Complete = served.Length > 0
            ? "✓ Complete: " + string.Join(", ", served.Select(c => Name(c.Capability) + (c.Providers.Count > 0 ? $" ({string.Join(", ", c.Providers)})" : "")))
            : null;
        foreach (var name in new[] { nameof(HasUsed), nameof(HasIdle), nameof(HasMissing), nameof(HasArchitecture) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>A candidate connection in the SCM card's picker; "" is automatic.</summary>
public sealed record ScmPick(string Name, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The project's SCM (app.js scmCard): the connection it uses and how it was chosen, a choice among the connections
/// serving its remote (kept on this machine), a test, and how agents called the scm_ tools. Tokens never reach it.
/// </summary>
public sealed partial class ScmCardSection(UiServices services, string projectId) : SectionViewModel("", projectId, ["activity"])
{
    private ScmStats? _stats;
    private bool _drawing;

    public ObservableCollection<ScmPick> Picks { get; } = [];
    public ObservableCollection<ToolUse> Tools { get; } = [];

    [ObservableProperty]
    public partial ProjectScm? Card { get; private set; }

    [ObservableProperty]
    public partial ScmPick? Pick { get; set; }

    [ObservableProperty]
    public partial ProjectScmTest? Test { get; private set; }

    [ObservableProperty]
    public partial bool Testing { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<DayBar> Bars { get; private set; } = [];

    public bool Configured => Card?.Configured is true;
    public bool NotConfigured => Card is { Configured: false };
    public bool Matched => Card?.Scm?.Status is "matched";
    public bool HasPicker => Card is { } c && (c.Candidates.Count > 1 || c.Choice is not null || c.ConfigName is not null);
    public bool PickerLocked => Card?.ConfigName is not null;
    public bool CanPick => HasPicker && !PickerLocked;

    public string PickNote =>
        Card?.ConfigName is { } name
            ? $"Set by [scm] name = \"{name}\" in this project's .harness/config.toml, which is shared with the team: change it there."
            : "Several connections serve this server; the choice is kept on this machine only.";

    public string TypeTag => Card?.Scm?.Type is { } t ? ScmTypes.Label(t) : "";
    public bool HasType => Card?.Scm?.Type is not null;
    public string Connection => Matched ? $"{Card!.Scm!.Name} · {Card.Scm.Address}" : "";
    public string ProjectLine
    {
        get
        {
            if (!Matched)
            {
                return "";
            }
            var remote = Card!.Scm!.Remote is { } r ? $" from remote {r}" : "";
            return $"Project {Card.Scm.Project}{remote} · {Card.Chosen}";
        }
    }
    public string Reason => Card?.Scm?.Reason ?? "No SCM serves this project.";

    public string Use
    {
        get
        {
            if (_stats is null)
            {
                return "";
            }
            var calls = _stats.Tools.Sum(t => t.Calls);
            return calls > 0
                ? $"{Shell.Plural(calls, "scm_ call")} · {Overview.Pct(_stats.Tools.Sum(t => t.Errors) / (double)calls)} failed"
                : $"not called in {_stats.Days} days";
        }
    }

    public bool HasTools => Tools.Count > 0;
    public string NoTools => _stats is null ? "" : $"Agents have not called the scm_ tools in {_stats.Days} days.";
    public string TestLine
    {
        get
        {
            if (Testing)
            {
                return "Testing…";
            }
            return Test is null ? "Not tested yet." : new ScmRow(new ScmEntry { Name = "", Type = "", Address = "", LastTest = Test.Test }).TestLine;
        }
    }
    public bool TestOk => Test?.Test.Ok is true;
    public string? ProjectCheck => Test?.Project.Detail;
    public bool ProjectOk => Test?.Project.Level is "ok";
    public string? ProjectFix => Test?.Project.Fix;
    public bool HasProjectCheck => Test is not null;
    public bool HasProjectFix => ProjectFix is { Length: > 0 };

    protected override async Task LoadCoreAsync(CancellationToken token) => Draw(await services.Api.ProjectScmAsync(ProjectId!, services.Host.Days, token));

    partial void OnPickChanged(ScmPick? value)
    {
        if (!_drawing && value is not null)
        {
            _ = ChooseAsync(value.Name);
        }
    }

    partial void OnTestingChanged(bool value) => OnPropertyChanged(nameof(TestLine));

    [RelayCommand]
    private async Task TestAsync()
    {
        Testing = true;
        try
        {
            Test = await services.Api.TestProjectScmAsync(ProjectId!, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
        finally
        {
            Testing = false;
        }
        Notify();
    }

    private async Task ChooseAsync(string name)
    {
        try
        {
            Draw(await services.Api.ChooseScmAsync(ProjectId!, name, CancellationToken.None));
            services.Toasts.Show(name.Length > 0 ? $"This project uses SCM {name} on this machine" : "SCM detected from the remotes again");
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }

    private void Draw(ProjectScm card)
    {
        _stats = card.Stats ?? _stats;
        Card = card;
        Test = card.LastTest;
        _drawing = true;
        Lists.Sync(
            Picks,
            new[] { new ScmPick("", "Automatic: detect it from the remotes") }.Concat(
                card.Candidates.Select(c => new ScmPick(c.Name, c.Missing ? $"{c.Name} (no longer configured)" : $"{c.Name} · {ScmTypes.Label(c.Type ?? "")} {c.Address} · {c.Token}"))
            )
        );
        Pick = Picks.FirstOrDefault(p => p.Name == (card.Choice ?? ""));
        _drawing = false;
        Lists.Sync(Tools, _stats?.Tools ?? []);
        Bars = DayCharts.Bars(_stats?.Trend);
        Notify();
    }

    private void Notify()
    {
        foreach (
            var name in new[]
            {
                nameof(Configured), nameof(NotConfigured), nameof(Matched), nameof(HasPicker), nameof(PickerLocked), nameof(CanPick), nameof(PickNote),
                nameof(TypeTag), nameof(HasType), nameof(Connection), nameof(ProjectLine), nameof(Reason), nameof(Use), nameof(HasTools), nameof(NoTools),
                nameof(TestLine), nameof(TestOk), nameof(ProjectCheck), nameof(ProjectOk), nameof(ProjectFix), nameof(HasProjectCheck), nameof(HasProjectFix),
            }
        )
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>A client's hook file: which hooks it has, and what is wrong.</summary>
public sealed record HookRow(ClientHook Hook, string Problem)
{
    public string Client => $"{Agents.Client(Hook.Client)} · {Hook.Scope}";
    public string Tool => Hook.Command is { Length: > 0 } ? "✓ tool events" : "○ tool events";
    public bool ToolOn => Hook.Command is { Length: > 0 };
    public bool ShowsSession => Hook.Client is "claude-code";
    public string Session => Hook.Session is { Length: > 0 } ? "✓ session start" : "○ session start";
    public bool SessionOn => Hook.Session is { Length: > 0 };
    public bool HasProblem => Problem.Length > 0;
}

/// <summary>The hooks (app.js hooksSection): the Git sync hooks and the client hooks as their files say, and a week's events.</summary>
public sealed partial class HooksSection(UiServices services, string projectId)
    : SectionViewModel("Hooks", projectId, ["activity"])
{
    public ObservableCollection<HookRow> Clients { get; } = [];
    public ObservableCollection<string> Files { get; } = [];

    [ObservableProperty]
    public partial string GitLine { get; private set; } = "";

    [ObservableProperty]
    public partial bool GitOk { get; private set; }

    [ObservableProperty]
    public partial string EventsLine { get; private set; } = "";

    [ObservableProperty]
    public partial string? Note { get; private set; }

    private string GitWords(HooksAnswer d, int on)
    {
        if (d.GitError is { Length: > 0 } error)
        {
            return error;
        }
        if (GitOk)
        {
            return $"✓ installed ({string.Join(", ", d.Git.Select(g => g.Name))})";
        }
        return on > 0
            ? $"✓ {on} of {d.Git.Count} installed: {string.Join(", ", d.Git.Where(g => !g.Installed).Select(g => g.Name))} missing"
            : "○ not installed: harness hooks install";
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var d = await services.Api.HooksAsync(ProjectId!, token);
        var on = d.Git.Count(g => g.Installed);
        GitOk = on == d.Git.Count && d.Git.Count > 0;
        GitLine = GitWords(d, on);
        Lists.Sync(
            Clients,
            d.Clients.Select(c => new HookRow(
                c,
                c.Error ?? c.Problem ?? (c.Command is not { Length: > 0 } && !d.Clients.Any(o => o.Client == c.Client && o.Command is { Length: > 0 }) ? "harness hooks install" : "")
            ))
        );
        var events = d.Events;
        EventsLine = events is { Events: > 0 }
            ? $"Last event {Shell.Ago(events.LastAt)}: the hooks run."
            : "No events recorded in 7 days: if they are installed, they may not run.";
        Lists.Sync(Files, d.Clients.Select(c => c.Path).Concat(d.GitDir is { } dir ? [dir] : []));
        Note = d.Note;
    }
}
