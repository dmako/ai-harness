using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>What a workspace's views share: the workspace, the projects registered on the machine, its members.</summary>
public sealed class WorkspaceContext(UiServices services, WorkspaceInfo workspace, IReadOnlyList<ProjectInfo> projects)
{
    public UiServices Services { get; } = services;
    public WorkspaceInfo Workspace { get; private set; } = workspace;
    public IReadOnlyList<ProjectInfo> Projects { get; private set; } = projects;
    public string Name => Workspace.Name;

    /// <summary>The members registered here, in the workspace's order.</summary>
    public IReadOnlyList<ProjectInfo> Members =>
        [.. Workspace.Members.Select(m => Projects.FirstOrDefault(p => p.Id == m.ProjectId)).OfType<ProjectInfo>()];

    public void Update(WorkspaceInfo workspace, IReadOnlyList<ProjectInfo> projects)
    {
        Workspace = workspace;
        Projects = projects;
    }

    /// <summary>A project's display name by its id (app.js pname), else the id.</summary>
    public string Label(string id) => Projects.FirstOrDefault(p => p.Id == id)?.DisplayName ?? id;

    /// <summary>A member by the folder or short name the server's answers name it with (app.js folderLabel).</summary>
    public string Folder(string folder) =>
        Projects.FirstOrDefault(p => p.Folder == folder || p.Name == folder)?.DisplayName ?? folder;

    /// <summary>The members a finding's task can go to.</summary>
    public IReadOnlyList<MemberChoice> Choices() =>
        [.. Workspace.Members.Select(m => new MemberChoice(m.ProjectId, m.Title is { Length: > 0 } t ? t : m.Project ?? m.ProjectId))];

    /// <summary>Triage of a finding between the members.</summary>
    public Task PostAsync(string action, FindingAction body, CancellationToken token) =>
        Services.Api.WorkspaceFindingAsync(Name, action, body, token);

    public FindingsBoard Board() =>
        new(Services, PostAsync, new KnowledgeActions(Services, null, []), Choices);
}

/// <summary>A member's card on the workspace's Overview (app.js memberTile): its state, what waits, context hits, agents.</summary>
public sealed record MemberTile(ProjectInfo Project)
{
    public string Name => Project.DisplayName;
    public string Href => Route.ProjectHref(Project.Id);
    public string Kind => Project.Live.Kind;
    public string StateIcon => Shell.StateIcon(Kind);
    public int Waiting => Project.Waiting;
    public bool HasWaiting => Waiting > 0;
    public string WaitingText => Waiting.ToString(CultureInfo.InvariantCulture);

    public string WaitParts
    {
        get
        {
            var l = Project.Live;
            var parts = new[]
            {
                l.KnowledgeWaiting > 0 ? $"{l.KnowledgeWaiting} knowledge" : null,
                l.WorkProposals > 0 ? $"{l.WorkProposals} tasks" : null,
                l.ChecksFailed > 0 ? $"{l.ChecksFailed} failing" : null,
            }.OfType<string>().ToArray();
            return parts.Length > 0 ? string.Join(" · ", parts) : "waiting";
        }
    }

    public bool HasContext => Project.Live.ContextAnswers30d > 0;
    public string ContextLine => $"{Overview.Pct(Project.Live.ContextHitRate30d)} context hits";
    public double HitRate => Project.Live.ContextHitRate30d;
    public string? Agents => Project.LastMcp is { Length: > 0 } at ? "agents " + Shell.Ago(at) : null;
    public bool HasAgents => Agents is not null;
}

/// <summary>A line of the workspace at a glance (app.js atAGlance): its sign, what it says, more, and where the detail is.</summary>
public sealed record GlanceLine(string Icon, string Text, string? More = null, string? Href = null)
{
    public bool HasMore => More is { Length: > 0 };
    public bool HasHref => Href is not null;
}

/// <summary>
/// A workspace's Overview (app.js workspaceView "overview"): a card per member, the ones waiting first, and the workspace
/// at a glance, read from the members.
/// </summary>
public sealed partial class WorkspaceOverviewViewModel : ObservableObject, IDisposable
{
    private const int Cap = 12;
    private readonly WorkspaceContext _context;

    public WorkspaceOverviewViewModel(WorkspaceContext context)
    {
        _context = context;
        Glance = new GlanceSection(context);
        Update();
    }

    public ObservableCollection<MemberTile> Tiles { get; } = [];
    public GlanceSection Glance { get; }

    [ObservableProperty]
    public partial string? AllLabel { get; private set; }

    public string AllHref => Route.WorkspaceHref(_context.Name, "members");
    public bool HasAll => AllLabel is not null;
    public bool NoMembers => Tiles.Count is 0;

    public void Start()
    {
        Glance.Follow(_context.Services.Live);
        _ = Glance.LoadAsync();
    }

    public void Update()
    {
        var sorted = _context.Members.OrderByDescending(p => p.Waiting).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        Lists.Sync(Tiles, sorted.Take(Cap).Select(p => new MemberTile(p)));
        AllLabel = sorted.Length > Cap ? $"All {sorted.Length} projects →" : null;
        OnPropertyChanged(nameof(HasAll));
        OnPropertyChanged(nameof(NoMembers));
    }

    public void Dispose() => Glance.Dispose();
}

/// <summary>
/// The workspace at a glance (app.js atAGlance): what is open, what it is made of, who everyone needs at run time,
/// circles of calls, databases, libraries used by everyone, versions behind or declared stale; only what applies,
/// each linking to where the detail is.
/// </summary>
public sealed partial class GlanceSection(WorkspaceContext context)
    : SectionViewModel("At a glance", null, ["summary", "findings", "sync", "workspaces", "registry", "levels"])
{
    public ObservableCollection<GlanceLine> Lines { get; } = [];
    public bool IsEmpty => Lines.Count is 0;

    private static async Task<T?> Try<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
        where T : class
    {
        try
        {
            return await read(token);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            return null;
        }
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var api = context.Services.Api;
        var name = context.Name;
        var sysTask = Try(t => api.SystemAsync(name, t), token);
        var stkTask = Try(t => api.WorkspaceStackAsync(name, t), token);
        var depTask = Try(t => api.DependenciesAsync(name, t), token);
        var fndTask = Try(t => api.RollupAsync(name, t), token);
        var issTask = Try(t => api.IssuesAsync(name, t), token);
        var (sys, stk, dep, fnd, iss) = (await sysTask, await stkTask, await depTask, await fndTask, await issTask);
        var lines = new List<GlanceLine>();
        Open(lines, fnd, iss);
        if (sys is not null)
        {
            System(lines, sys, stk);
        }
        if (dep is not null)
        {
            Libraries(lines, dep);
        }
        if (stk is not null)
        {
            Stale(lines, stk);
        }
        Lists.Sync(Lines, lines);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string Arch => Route.WorkspaceHref(context.Name, "architecture");
    private string VersionsHref => Route.WorkspaceHref(context.Name, "issues", "versions");

    /// <summary>What is open, first: findings and design issues together.</summary>
    private void Open(List<GlanceLine> lines, RollupAnswer? fnd, IssuesAnswer? iss)
    {
        if (fnd is null && iss is null)
        {
            return;
        }
        var rows = fnd?.Rows ?? [];
        var issues = (iss?.Issues ?? []).Where(x => x.Kind is not ("local-copy" or "duplicate")).ToArray();
        int Count(string level) => rows.Sum(r => r.Levels.GetValueOrDefault(level)) + issues.Count(x => x.Level == level);
        var (p, w, n) = (Count("problem"), Count("warn"), Count("info"));
        var worst = rows.Where(r => r.Kind is "member").OrderByDescending(Rollup.Severity).FirstOrDefault();
        var words = new[] { p > 0 ? Shell.Plural(p, "problem") : null, w > 0 ? Shell.Plural(w, "warning") : null, n > 0 ? Shell.Plural(n, "note") : null }
            .OfType<string>()
            .ToArray();
        if (words.Length is 0)
        {
            lines.Add(new GlanceLine("✓", "Nothing open", "· no findings or design issues"));
            return;
        }
        string icon;
        if (p > 0)
        {
            icon = "✕";
        }
        else
        {
            icon = w > 0 ? "▲" : "○";
        }
        lines.Add(
            new GlanceLine(
                icon,
                $"{string.Join(", ", words)} open",
                worst is not null && Rollup.Severity(worst) > 0 ? $"· most in {worst.Name}" : null,
                Route.WorkspaceHref(context.Name, "issues")
            )
        );
    }

    private void System(List<GlanceLine> lines, SystemAnswer sys, WorkspaceStackAnswer? stk)
    {
        var services = sys.Nodes.Where(n => n.Kind is "service" or "frontend").ToArray();
        var libs = sys.Nodes.Where(n => n.Kind is "library").ToArray();
        var outside = sys.Nodes.Where(n => n.Kind is "external" or "broker").ToArray();
        var parts = new[]
        {
            Shell.Plural(services.Length, "service"),
            Libraries(libs.Length),
            outside.Length > 0 ? Shell.Plural(outside.Length, "outside system") : null,
        }.OfType<string>();
        var badges = services.Concat(libs)
            .Select(n => n.MemberId is { } id && stk?.Stacks.GetValueOrDefault(id)?.Badge is { } b ? b : null)
            .OfType<string>()
            .GroupBy(b => b)
            .ToArray();
        string? more = null;
        if (badges.Length is 1)
        {
            more = $"· all {badges[0].Key}";
        }
        else if (badges.Length > 1)
        {
            more = "· " + string.Join(", ", badges.Select(g => $"{g.Key} ({g.Count()})"));
        }
        lines.Add(new GlanceLine("▦", $"Made of {string.Join(", ", parts)}", more));
        var calls = sys.Links.Where(l => l.Style is "grpc" or "http").ToArray();
        foreach (var n in services)
        {
            var callers = calls.Where(l => l.To == n.Id && services.Any(x => x.Id == l.From)).Select(l => l.From).Distinct().Count();
            if (services.Length > 2 && callers == services.Length - 1)
            {
                lines.Add(new GlanceLine("⇣", $"Every other service calls {n.Name}", "· it is the one they cannot run without", Arch));
            }
        }
        if (sys.Smells.FirstOrDefault(x => x.Kind is "sync-cycle") is { } cycle)
        {
            lines.Add(new GlanceLine("▲", cycle.Title, null, Arch));
        }
        var dbs = sys.Nodes.Where(n => n.Kind is "database").ToArray();
        if (dbs.Length > 0)
        {
            var tenant = dbs.Count(d => d.Detail is "per tenant");
            var shared = sys.Smells.Count(x => x.Kind is "shared-database");
            lines.Add(
                new GlanceLine(
                    "⛁",
                    $"{Shell.Plural(dbs.Length, "database")} in the code" + (tenant > 0 ? $", {tenant} of them per tenant" : ""),
                    shared > 0 ? $"· {shared} shared between services" : "· each owned by one service",
                    Arch
                )
            );
        }
    }

    private static string? Libraries(int n) =>
        n switch
        {
            0 => null,
            1 => "1 library",
            _ => $"{n} libraries",
        };

    /// <summary>Libraries used by everyone (build time).</summary>
    private void Libraries(List<GlanceLine> lines, DependencyAnswer dep)
    {
        var of = dep.Nodes.Count - 1;
        foreach (var id in dep.Nodes.Where(x => x.Library).Select(n => n.ProjectId))
        {
            var k = dep.Edges.Where(e => e.To == id).Select(e => e.From).Distinct().Count();
            if (k > 0)
            {
                lines.Add(
                    new GlanceLine(
                        "◇",
                        $"{context.Label(id)} is used by {(k == of ? $"all {k}" : $"{k} of {of}")} other members",
                        "· a library, used through its packages",
                        Route.WorkspaceHref(context.Name, "architecture", "build")
                    )
                );
            }
        }
    }

    /// <summary>Versions behind, or declared stale; runtimes that differ.</summary>
    private void Stale(List<GlanceLine> lines, WorkspaceStackAnswer stk)
    {
        var behind = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var r in stk.Rows.Where(x => x.Kind is "core" && x.Published is not null))
        {
            foreach (var (id, vs) in r.Versions)
            {
                if (vs.Any(v => Versions.Compare(v, r.Published!) < 0))
                {
                    if (!behind.TryGetValue(id, out var owners))
                    {
                        behind[id] = owners = [];
                    }
                    owners.Add(context.Label(r.Owner ?? ""));
                }
                if (vs.Any(v => Versions.Compare(v, r.Published!) > 0) && !stale.Contains(r.Name))
                {
                    stale.Add(r.Name);
                }
            }
        }
        if (behind.Count > 0)
        {
            var owners = string.Join(", ", behind.Values.SelectMany(x => x).Distinct());
            lines.Add(
                new GlanceLine(
                    "↺",
                    $"{behind.Count} member{(behind.Count is 1 ? " uses" : "s use")} older versions of packages from {owners}",
                    "· than their owners declare now",
                    VersionsHref
                )
            );
        }
        if (stale.Count > 0)
        {
            lines.Add(
                new GlanceLine(
                    "?",
                    $"{stale.Count} package version declaration{(stale.Count is 1 ? " looks" : "s look")} stale",
                    "· " + string.Join(", ", stale.Take(2)) + (stale.Count > 2 ? "…" : ""),
                    VersionsHref
                )
            );
        }
        foreach (var r in stk.Rows.Where(x => x.Kind is "runtime" && x.Differ))
        {
            lines.Add(new GlanceLine("↺", $"{r.Name} versions differ", "· " + string.Join(", ", r.Versions.Values.SelectMany(v => v).Distinct()), VersionsHref));
        }
    }
}

/// <summary>A member in Members (app.js membersManage row): chosen or not, its state, what waits, when synced and used.</summary>
public sealed partial class MemberRow(WorkspaceMember member, ProjectInfo? project, string path, string? stack) : ObservableObject
{
    public WorkspaceMember Member { get; } = member;
    public ProjectInfo? Project { get; } = project;
    public string Id => Member.ProjectId;
    public string Name => Project?.DisplayName ?? Member.ProjectId;
    public string? Href => Project is null ? null : Route.ProjectHref(Project.Id);
    public bool Registered => Project is not null;
    public string State => Project?.Live.Words ?? "not registered";
    public string Kind => Project?.Live.Kind ?? "bad";
    public string Stack => stack ?? "—";
    public string Waiting => Project is { Waiting: > 0 } p ? p.Waiting.ToString(CultureInfo.InvariantCulture) : "—";
    public string Synced => Project?.Live.SyncedAt is { Length: > 0 } at ? Shell.Ago(at) : "never";
    public string Used => Project?.LastMcp is { Length: > 0 } at ? Shell.Ago(at) : "—";
    public string Context => Project is { Live.ContextAnswers30d: > 0 } p ? Overview.Pct(p.Live.ContextHitRate30d) : "—";
    public string Path { get; } = path;
    public string FullPath => Member.Root ?? "";

    [ObservableProperty]
    public partial bool Chosen { get; set; }
}

/// <summary>
/// A workspace's Members (app.js membersManage): its projects with a filter and a choice of them to take out, adding
/// projects, and deleting the workspace; every change reviewed before it is sent.
/// </summary>
public sealed partial class WorkspaceMembersViewModel : ObservableObject
{
    private readonly WorkspaceContext _context;
    private IReadOnlyDictionary<string, ProjectStack> _stacks = new Dictionary<string, ProjectStack>();
    private bool _drawing;

    public WorkspaceMembersViewModel(WorkspaceContext context)
    {
        _context = context;
        Update();
    }

    public ObservableCollection<MemberRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool AllChosen { get; set; }

    [ObservableProperty]
    public partial string? Common { get; private set; }

    [ObservableProperty]
    public partial int ChosenCount { get; private set; }

    public bool HasCommon => Common is not null;
    public string CommonLine => $"Paths under {Common}";
    public bool CanRemove => ChosenCount > 0;
    public string RemoveLabel => ChosenCount > 0 ? $"Remove selected ({ChosenCount})" : "Remove selected";
    public bool CanAdd => Others.Count > 0;
    public bool IsEmpty => Rows.Count is 0;

    private IReadOnlyList<ProjectInfo> Others => [.. _context.Projects.Where(p => _context.Workspace.Members.All(m => m.ProjectId != p.Id))];

    partial void OnQueryChanged(string value) => Draw();

    partial void OnAllChosenChanged(bool value)
    {
        if (_drawing)
        {
            return;
        }
        foreach (var row in Rows)
        {
            row.Chosen = value;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            _stacks = (await _context.Services.Api.WorkspaceStackAsync(_context.Name, CancellationToken.None)).Stacks;
            Draw();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            // the stacks are a convenience: the table stands without them
        }
    }

    public void Update()
    {
        var roots = _context.Workspace.Members.Select(m => m.Root).OfType<string>().Where(r => r.Length > 0).ToArray();
        Common = CommonFolder(roots);
        OnPropertyChanged(nameof(HasCommon));
        OnPropertyChanged(nameof(CommonLine));
        OnPropertyChanged(nameof(CanAdd));
        Draw();
    }

    /// <summary>The folder every member lives under, shown once.</summary>
    public static string? CommonFolder(IReadOnlyList<string> roots)
    {
        if (roots.Count < 2)
        {
            return null;
        }
        var common = string.Join('/', roots[0].Replace('\\', '/').Split('/')[..^1]);
        foreach (var r in roots.Select(r => r.Replace('\\', '/')))
        {
            while (common.Length > 0 && !r.StartsWith(common + "/", StringComparison.Ordinal))
            {
                common = string.Join('/', common.Split('/')[..^1]);
            }
        }
        return common.Length > 0 ? common : null;
    }

    private void Draw()
    {
        var chosen = Rows.Where(r => r.Chosen).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in Rows)
        {
            row.PropertyChanged -= Changed;
        }
        var q = Query.Trim();
        var rows = _context.Workspace.Members.Select(m =>
            {
                var p = _context.Projects.FirstOrDefault(x => x.Id == m.ProjectId);
                var root = (m.Root ?? "").Replace('\\', '/');
                var path = Relative(root);
                return new MemberRow(m, p, path, _stacks.GetValueOrDefault(m.ProjectId)?.Badge) { Chosen = chosen.Contains(m.ProjectId) };
            })
            .Where(r => q.Length is 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.FullPath.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Rows.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += Changed;
            Rows.Add(row);
        }
        Count();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string Relative(string root)
    {
        if (root.Length is 0)
        {
            return "no longer registered on this machine";
        }
        return Common is { } c && root.StartsWith(c + "/", StringComparison.Ordinal) ? root[(c.Length + 1)..] : root;
    }

    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MemberRow.Chosen))
        {
            Count();
        }
    }

    private void Count()
    {
        ChosenCount = Rows.Count(r => r.Chosen);
        _drawing = true;
        AllChosen = Rows.Count > 0 && Rows.All(r => r.Chosen);
        _drawing = false;
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(RemoveLabel));
    }

    /// <summary>The chosen members out of the workspace, after a review; their own state stays as it is.</summary>
    [RelayCommand]
    private async Task RemoveAsync()
    {
        var chosen = Rows.Where(r => r.Chosen).ToArray();
        if (chosen.Length is 0)
        {
            return;
        }
        var review = new StringBuilder();
        foreach (var row in chosen)
        {
            review.Append("− ").AppendLine(row.Name);
        }
        review.AppendLine("Their own state and the other members stay as they are.").Append(WorkspaceText.Note);
        var confirm = new ConfirmDialog
        {
            Title = $"Remove {Shell.Plural(chosen.Length, "project")} from {_context.Workspace.DisplayName}?",
            Message = review.ToString(),
            AcceptLabel = "Remove",
            Danger = true,
        };
        if (!await _context.Services.Dialogs.ConfirmAsync(confirm))
        {
            return;
        }
        try
        {
            foreach (var row in chosen)
            {
                await _context.Services.Api.LeaveAsync(_context.Name, row.Id, CancellationToken.None);
            }
            _context.Services.Toasts.Show($"{Shell.Plural(chosen.Length, "project")} removed");
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _context.Services.Toasts.Fail(exception.Message);
        }
        _context.Services.Live.RefreshAll();
    }

    [RelayCommand]
    private Task<bool> AddAsync() =>
        _context.Services.Dialogs.ShowAsync(
            new AddProjectsDialog(_context.Services, _context.Workspace, Others)
            {
                Title = $"Add projects to {_context.Workspace.DisplayName}",
                Wide = true,
                AcceptLabel = "Add projects",
            }
        );

    /// <summary>The workspace deleted, after a review: its projects keep their state; its knowledge no longer applies.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        var w = _context.Workspace;
        var confirm = new ConfirmDialog
        {
            Title = $"Delete workspace {w.DisplayName}?",
            Message =
                $"The workspace and its membership list go; its {Shell.Plural(w.Members.Count, "project")} keep all their state.\nKnowledge recorded at workspace scope stays on disk but no longer applies to any project.\n{WorkspaceText.Note}",
            AcceptLabel = "Delete workspace",
            Danger = true,
        };
        if (!await _context.Services.Dialogs.ConfirmAsync(confirm))
        {
            return;
        }
        try
        {
            await _context.Services.Api.DeleteWorkspaceAsync(w.Name, CancellationToken.None);
            _context.Services.Toasts.Show("Workspace deleted");
            _context.Services.Host.Navigation.Go("#/");
            _context.Services.Live.RefreshAll();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _context.Services.Toasts.Fail(exception.Message);
        }
    }
}

/// <summary>The words workspace writes share.</summary>
public static partial class WorkspaceText
{
    public const string Note = "Only the machine's workspace list (workspaces.json) changes; no project's .harness state is touched.";

    /// <summary>A short name from a display name (app.js slug): lower case, '.', '_' and '-' kept, the rest a '-'.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Short names are lower case.")]
    public static string Slug(string title)
    {
        var plain = new string([.. title.ToLowerInvariant().Normalize(NormalizationForm.FormKD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) is not UnicodeCategory.NonSpacingMark)]);
        var slug = NotShort().Replace(plain, "-").Trim('-');
        return slug.Length > 64 ? slug[..64] : slug;
    }

    public static bool IsShortName(string name) => ShortName().IsMatch(name);

    [GeneratedRegex("[^a-z0-9._-]+", RegexOptions.None, 1000)]
    private static partial Regex NotShort();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.None, 1000)]
    private static partial Regex ShortName();
}

/// <summary>A project to choose (app.js projectPicker row): its name, folder, and whether it is chosen.</summary>
public sealed partial class PickRow(ProjectInfo project) : ObservableObject
{
    public ProjectInfo Project { get; } = project;
    public string Name => Project.DisplayName;
    public string Root => Project.Root;

    [ObservableProperty]
    public partial bool Chosen { get; set; }
}

/// <summary>
/// Choosing projects (app.js projectPicker): a filter, a row each, choose what is shown or none, and those registered
/// under a folder.
/// </summary>
public sealed partial class ProjectPicker : ObservableObject
{
    private readonly UiServices _services;
    private readonly IReadOnlyList<PickRow> _all;

    public ProjectPicker(UiServices services, IReadOnlyList<ProjectInfo> projects)
    {
        _services = services;
        _all = [.. projects.Select(p => new PickRow(p))];
        foreach (var row in _all)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PickRow.Chosen))
                {
                    OnPropertyChanged(nameof(ChosenLine));
                }
            };
        }
        Lists.Sync(Rows, _all);
    }

    public ObservableCollection<PickRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Folder { get; set; } = "";

    [ObservableProperty]
    public partial string? FolderMessage { get; private set; }

    public IReadOnlyList<ProjectInfo> Chosen => [.. _all.Where(r => r.Chosen).Select(r => r.Project)];
    public string ChosenLine => $"{_all.Count(r => r.Chosen)} chosen";
    public bool IsEmpty => Rows.Count is 0;

    partial void OnQueryChanged(string value)
    {
        var q = value.Trim();
        Lists.Sync(Rows, _all.Where(r => q.Length is 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Root.Contains(q, StringComparison.OrdinalIgnoreCase)));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ChooseShown()
    {
        foreach (var row in Rows)
        {
            row.Chosen = true;
        }
    }

    [RelayCommand]
    private void ChooseNone()
    {
        foreach (var row in _all)
        {
            row.Chosen = false;
        }
    }

    /// <summary>The registered projects under a folder, chosen.</summary>
    [RelayCommand]
    private async Task FindAsync()
    {
        try
        {
            var found = await _services.Api.DiscoverAsync(Folder.Trim(), CancellationToken.None);
            var ids = found.Projects.Select(p => p.ProjectId).ToHashSet(StringComparer.Ordinal);
            var rows = _all.Where(r => ids.Contains(r.Project.Id)).ToArray();
            foreach (var row in rows)
            {
                row.Chosen = true;
            }
            FolderMessage = rows.Length > 0 ? $"✓ {Shell.Plural(rows.Length, "project")} under {found.Folder} chosen" : $"No registered project under {found.Folder}.";
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            FolderMessage = exception.Message;
        }
    }
}

/// <summary>
/// A new workspace (app.js workspaceWizard): its display name, its short name (following the display name until it is
/// edited), its projects, then a review before it is created.
/// </summary>
public sealed partial class WorkspaceFormDialog : FormDialog<string>
{
    private readonly UiServices _services;
    private readonly IReadOnlyList<WorkspaceInfo> _workspaces;
    private bool _edited;
    private bool _following;

    public WorkspaceFormDialog(UiServices services, IReadOnlyList<ProjectInfo> projects, IReadOnlyList<WorkspaceInfo> workspaces)
    {
        _services = services;
        _workspaces = workspaces;
        Picker = new ProjectPicker(services, projects);
    }

    public ProjectPicker Picker { get; }

    [ObservableProperty]
    public partial string DisplayTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ShortName { get; set; } = "";

    public string Preview => $"workspace:{(ShortName.Length > 0 ? ShortName : "…")}";

    partial void OnDisplayTitleChanged(string value)
    {
        if (_edited)
        {
            return;
        }
        _following = true;
        ShortName = WorkspaceText.Slug(value.Trim());
        _following = false;
    }

    partial void OnShortNameChanged(string value)
    {
        if (!_following)
        {
            _edited = true;
        }
        OnPropertyChanged(nameof(Preview));
    }

    private string Title2 => string.Join(' ', DisplayTitle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    protected override string? Validate()
    {
        var name = ShortName.Trim();
        if (_workspaces.Any(w => w.Name == name))
        {
            return $"A workspace {name} exists already.";
        }
        return WorkspaceText.IsShortName(name) ? null : "Short name: lower-case letters, digits, '.', '_' or '-', starting with a letter or digit.";
    }

    protected override string? Review()
    {
        var chosen = Picker.Chosen;
        var text = new StringBuilder();
        text.Append(Title2.Length > 0 ? Title2 : ShortName.Trim()).Append("  (workspace:").Append(ShortName.Trim()).AppendLine(")");
        if (chosen.Count is 0)
        {
            text.AppendLine("No projects yet: add them later on the workspace's Members.");
        }
        else
        {
            text.AppendLine(Shell.Plural(chosen.Count, "project") + ":");
            foreach (var p in chosen)
            {
                text.Append("+ ").AppendLine(p.DisplayName);
            }
        }
        return text.Append(WorkspaceText.Note).ToString();
    }

    protected override async Task<string> SendAsync(CancellationToken token)
    {
        var name = ShortName.Trim();
        await _services.Api.CreateWorkspaceAsync(new WorkspaceCreate { Name = name, Title = Title2, ProjectIds = [.. Picker.Chosen.Select(p => p.Id)] }, token);
        _services.Toasts.Show("Workspace created");
        _services.Host.Navigation.Go(Route.WorkspaceHref(name));
        _services.Live.RefreshAll();
        return name;
    }
}

/// <summary>Projects added to a workspace (app.js addProjectsWizard): choose, then review.</summary>
public sealed partial class AddProjectsDialog(UiServices services, WorkspaceInfo workspace, IReadOnlyList<ProjectInfo> others) : FormDialog<bool>
{
    public ProjectPicker Picker { get; } = new(services, others);

    protected override string? Validate() => Picker.Chosen.Count is 0 ? "Choose at least one project." : null;

    protected override string? Review()
    {
        var text = new StringBuilder().Append("Joining ").AppendLine(workspace.DisplayName);
        foreach (var p in Picker.Chosen)
        {
            text.Append("+ ").AppendLine(p.DisplayName);
        }
        return text.Append(WorkspaceText.Note).ToString();
    }

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        var chosen = Picker.Chosen;
        await services.Api.JoinAsync(workspace.Name, [.. chosen.Select(p => p.Id)], token);
        services.Toasts.Show($"{Shell.Plural(chosen.Count, "project")} added");
        services.Live.RefreshAll();
        return true;
    }
}
