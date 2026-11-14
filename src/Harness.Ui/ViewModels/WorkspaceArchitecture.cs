using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Controls;
using Harness.Ui.Navigation;

namespace Harness.Ui.ViewModels;

/// <summary>How calls are named and coloured (app.js STYLE_LABEL, the st-* line colours).</summary>
public static class CallStyles
{
    public static string Label(string style) =>
        style switch
        {
            "grpc" => "gRPC",
            "http" => "HTTP",
            "messaging" => "messaging",
            _ => style,
        };

    public static string Tone(string style) =>
        style switch
        {
            "grpc" => "CDecision",
            "http" => "Accent",
            "messaging" => "CGoal",
            _ => "Axis",
        };

    /// <summary>A contract's short name: IOrdersGrpcService is Orders.</summary>
    public static string Brief(string contract)
    {
        var name = contract.StartsWith('I', StringComparison.Ordinal) && contract.Length > 1 && char.IsUpper(contract[1]) ? contract[1..] : contract;
        foreach (var end in new[] { "GrpcService", "Service" })
        {
            if (name.EndsWith(end, StringComparison.Ordinal) && name.Length > end.Length)
            {
                return name[..^end.Length];
            }
        }
        return name;
    }
}

/// <summary>A call style in the legend.</summary>
public sealed record StyleKey(string Label, string Tone);

/// <summary>A service in the side list when nothing is selected: whom it calls, who calls it, its databases.</summary>
public sealed record ServiceLine(string Id, string Name, string Line);

/// <summary>A call of the selected service, to or from another: its style, contracts, what declares it and its evidence.</summary>
public sealed record CallRow(string Other, string Name, SystemLink Link)
{
    public string Style => CallStyles.Label(Link.Style);
    public string Tone => CallStyles.Tone(Link.Style);

    public string? ContractsLine =>
        Link.Contracts is { Count: > 0 } cs ? $"{Shell.Plural(cs.Count, "contract")}: {Cut(cs.Select(CallStyles.Brief).ToArray(), 4)}" : null;

    public bool HasContracts => ContractsLine is not null;

    public string? DeclaredLine
    {
        get
        {
            var declared = Link.What.Where(x => !(Link.Contracts ?? []).Contains(x)).ToArray();
            if (declared.Length is 0)
            {
                return null;
            }
            var basis = Link.Basis is { Length: > 0 } b ? $" (matched by {b})" : "";
            return "declared by " + Cut(declared, 3) + basis;
        }
    }

    public bool HasDeclared => DeclaredLine is not null;
    public bool HasEvidence => Link.Evidence.Count > 0;
    public string EvidenceHead => $"evidence ({Link.Evidence.Count})";

    /// <summary>The first <paramref name="n"/> of a list and how many more (app.js listCut).</summary>
    public static string Cut(IReadOnlyList<string> xs, int n) =>
        xs.Count <= n ? string.Join(", ", xs) : $"{string.Join(", ", xs.Take(n))} +{xs.Count - n} more";
}

/// <summary>A database of the selected service: its name, one per tenant or what it is, and what declares it.</summary>
public sealed record DatabaseRow(string Name, string? Detail, string? Declared)
{
    public bool HasDetail => Detail is not null;
    public bool HasDeclared => Declared is not null;
}

/// <summary>Members using another's contracts (app.js cpair): the callers, the member serving them, the contracts.</summary>
public sealed record ContractPair(string Clients, string Servers, string Line, IReadOnlyList<string> Contracts);

/// <summary>A contract served and used by no other member.</summary>
public sealed record ContractRow(string Name, string Declared, string Owner, string Servers, string Clients);

/// <summary>
/// Runtime: calls (app.js systemView): the workspace as a system, its services, front-ends, outside systems, brokers
/// and databases and how they communicate, read from the members' code and configuration; its smells with their
/// triage, the map (callers above the called), the selected service beside it, and the gRPC contracts.
/// </summary>
public sealed partial class SystemSection : SectionViewModel
{
    private readonly WorkspaceContext _context;
    private SystemAnswer? _system;

    public SystemSection(WorkspaceContext context)
        : base("", null, ["sync", "findings", "levels"])
    {
        _context = context;
        Board = context.Board();
        Board.Okay = "✓ No system smells: no circle of synchronous calls, every contract has both sides, no database is shared.";
    }

    public FindingsBoard Board { get; }
    public ObservableCollection<StyleKey> Legend { get; } = [];
    public ObservableCollection<ServiceLine> Services { get; } = [];
    public ObservableCollection<CallRow> Calls { get; } = [];
    public ObservableCollection<CallRow> CalledBy { get; } = [];
    public ObservableCollection<DatabaseRow> Databases { get; } = [];
    public ObservableCollection<ContractPair> Pairs { get; } = [];
    public ObservableCollection<ContractRow> Idle { get; } = [];

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string SummaryMore { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<GraphRow> Rows { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<GraphEdge> Edges { get; private set; } = [];

    [ObservableProperty]
    public partial string? Selected { get; set; }

    [ObservableProperty]
    public partial string? Silent { get; private set; }

    public bool HasSelection => SelectedNode is not null;
    public bool NoSelection => SelectedNode is null;
    public SystemNode? SelectedNode => Selected is { } id ? _system?.Nodes.FirstOrDefault(n => n.Id == id) : null;
    public string SelectedName => SelectedNode?.Name ?? "";

    public string SelectedKind =>
        SelectedNode is { Kind: "external" } n ? $"outside the workspace, declared by {n.Detail}" : SelectedNode?.Kind ?? "";

    public string? SelectedHref => SelectedNode?.MemberId is { } id ? Route.ProjectHref(id, "architecture") : null;
    public bool HasSelectedHref => SelectedHref is not null;
    public string CallsHead => $"Calls ({Calls.Count})";
    public string CalledByHead => SelectedNode?.Kind is "broker" ? $"Used by ({CalledBy.Count})" : $"Called by ({CalledBy.Count})";
    public string DatabasesHead => $"Databases ({Databases.Count})";
    public bool HasCalls => Calls.Count > 0;
    public bool HasCalledBy => CalledBy.Count > 0;
    public bool HasDatabases => Databases.Count > 0;
    public bool HasSilent => Silent is not null;
    public bool HasContracts => Pairs.Count > 0 || Idle.Count > 0;
    public bool HasIdle => Idle.Count > 0;
    public string IdleHead => $"{Shell.Plural(Idle.Count, "contract")} served, used by no other member";
    public bool HasMap => Rows.Count > 0;

    partial void OnSelectedChanged(string? value) => DrawSide();

    [RelayCommand]
    private void Pick(string id) => Selected = id;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var s = await _context.Services.Api.SystemAsync(_context.Name, token);
        _system = s;
        int Count(string kind) => s.Nodes.Count(n => n.Kind == kind);
        var fe = Count("frontend");
        Summary = $"{Count("service") + fe} services" + (fe > 0 ? $" ({Shell.Plural(fe, "front-end")})" : "");
        SummaryMore =
            $" · {Shell.Plural(Count("external"), "outside system")} · {Shell.Plural(Count("database"), "database")} in code"
            + (Count("broker") > 0 ? $" · {Shell.Plural(Count("broker"), "broker")}" : "")
            + $" · {s.Contracts.Count} gRPC contracts";
        // repeated code is Shared code's
        IReadOnlyList<Finding> Own(IReadOnlyList<Finding> xs) => [.. xs.Where(x => x.Kind is not "shared-code")];
        Board.Show(s.Findings is { Count: > 0 } f ? Own(f) : Own(s.Smells), s.Snapshot, s.Findings is not null);
        Draw(s);
        DrawSide();
    }

    private static IEnumerable<SystemLink> CallsOf(SystemAnswer s) => s.Links.Where(l => l.Style is not "database");

    private static IReadOnlyList<SystemNode> DatabasesOf(SystemAnswer s, string id) =>
        [.. s.Links.Where(l => l.From == id && l.Style is "database").Select(l => s.Nodes.FirstOrDefault(n => n.Id == l.To)).OfType<SystemNode>()];

    private string Label(string id) => _system?.Nodes.FirstOrDefault(n => n.Id == id)?.Name ?? id;

    /// <summary>The map: callers above the services they call, outside systems and brokers in a row of their own.</summary>
    private void Draw(SystemAnswer s)
    {
        var calls = CallsOf(s).ToArray();
        var talking = calls.SelectMany(l => new[] { l.From, l.To }).ToHashSet(StringComparer.Ordinal);
        var members = s.Nodes.Where(n => n.Kind is "service" or "frontend").ToArray();
        var silent = members.Where(n => !talking.Contains(n.Id) && DatabasesOf(s, n.Id).Count is 0).ToArray();
        var shown = members.Except(silent).ToArray();
        var others = shown.Length - 1;
        GraphNode Card(SystemNode n)
        {
            var callers = calls.Where(l => l.To == n.Id && members.Any(m => m.Id == l.From)).Select(l => l.From).Distinct().Count();
            var hub = others > 1 && callers == others;
            var dbs = DatabasesOf(s, n.Id);
            var badge = string.Join(
                " · ",
                new[] { hub ? "every other service calls it" : null, dbs.Count > 0 ? "⛁ " + string.Join(", ", dbs.Select(d => d.Name + (d.Detail is "per tenant" ? " (per tenant)" : ""))) : null }.OfType<string>()
            );
            return new GraphNode(n.Id, n.Name, $"calls {calls.Count(l => l.From == n.Id)} · called by {callers}", badge.Length > 0 ? badge : null, n.Name);
        }
        var layers = shown.Select(n => n.Layer).Distinct().OrderByDescending(l => l).ToArray();
        var rows = new List<GraphRow>();
        for (var i = 0; i < layers.Length; i++)
        {
            var label = "";
            if (layers.Length > 1 && i == 0)
            {
                label = "callers";
            }
            else if (layers.Length > 1 && i == layers.Length - 1)
            {
                label = "called";
            }
            rows.Add(new GraphRow(label, [.. shown.Where(n => n.Layer == layers[i]).Select(Card)]));
        }
        var outside = s.Nodes.Where(n => n.Kind is "external" or "broker").ToArray();
        if (outside.Length > 0)
        {
            rows.Add(
                new GraphRow(
                    "outside",
                    [
                        .. outside.Select(n =>
                            new GraphNode(
                                n.Id,
                                n.Kind is "broker" ? "⇄ " + n.Name : n.Name,
                                n.Kind is "broker" ? "broker" : "outside the workspace",
                                null,
                                n.Detail is { Length: > 0 } d ? $"declared by {d}" : n.Name
                            )
                        ),
                    ]
                )
            );
        }
        Rows = rows;
        Edges =
        [
            .. calls.Select(l =>
                new GraphEdge(l.From, l.To, Tone: CallStyles.Tone(l.Style), Tip: $"{Label(l.From)} → {Label(l.To)} ({CallStyles.Label(l.Style)}): {string.Join(", ", l.What)}")
            ),
        ];
        Lists.Sync(Legend, new[] { "grpc", "http", "messaging" }.Where(st => calls.Any(l => l.Style == st)).Select(st => new StyleKey(CallStyles.Label(st), CallStyles.Tone(st))));
        Silent = silent.Length > 0 ? $"Services with no call, configured address or database found: {string.Join(", ", silent.Select(n => n.Name))}." : null;
        Lists.Sync(
            Services,
            members.Select(n =>
            {
                var outs = calls.Where(l => l.From == n.Id).Select(l => Label(l.To)).Distinct().ToArray();
                var ins = calls.Where(l => l.To == n.Id).Select(l => Label(l.From)).Distinct().ToArray();
                var dbs = DatabasesOf(s, n.Id).Count;
                var line = string.Join(
                    " · ",
                    new[] { outs.Length > 0 ? "calls " + string.Join(", ", outs) : null, ins.Length > 0 ? "called by " + string.Join(", ", ins) : null, dbs > 0 ? Shell.Plural(dbs, "database") : null }.OfType<string>()
                );
                return new ServiceLine(n.Id, n.Name, line.Length > 0 ? line : "no calls found");
            })
        );
        Contracts(s);
        OnPropertyChanged(nameof(HasSilent));
        OnPropertyChanged(nameof(HasMap));
    }

    /// <summary>The contracts used between members by who uses whom (one line per pair), the rest folded.</summary>
    private void Contracts(SystemAnswer s)
    {
        string Names(IReadOnlyList<string> ids, string none) => ids.Count > 0 ? string.Join(", ", ids.Select(Label)) : none;
        var used = s.Contracts.Where(c => c.Clients.Count > 0 || c.Servers.Count is 0).ToArray();
        var idle = s.Contracts.Where(c => c.Clients.Count is 0 && c.Servers.Count > 0).ToArray();
        Lists.Sync(
            Pairs,
            used.GroupBy(c => (Clients: Names(c.Clients, "no member"), Servers: Names(c.Servers, "nobody"), Owner: Label(c.Owner)))
                .Select(g =>
                    new ContractPair(
                        g.Key.Clients,
                        g.Key.Servers,
                        $" · {Shell.Plural(g.Count(), "contract")}" + (g.Key.Owner != g.Key.Servers ? $", declared in {g.Key.Owner}" : ""),
                        [.. g.Select(c => CallStyles.Brief(c.Name))]
                    )
                )
        );
        Lists.Sync(Idle, idle.Select(c => new ContractRow(c.Name, c.Declared ?? "", Label(c.Owner), Names(c.Servers, "none"), Names(c.Clients, "no member"))));
        OnPropertyChanged(nameof(HasContracts));
        OnPropertyChanged(nameof(HasIdle));
        OnPropertyChanged(nameof(IdleHead));
    }

    private void DrawSide()
    {
        var s = _system;
        var id = SelectedNode?.Id;
        var calls = s is null ? [] : CallsOf(s).ToArray();
        Lists.Sync(Calls, id is null ? [] : calls.Where(l => l.From == id).Select(l => new CallRow(l.To, Label(l.To), l)));
        Lists.Sync(CalledBy, id is null ? [] : calls.Where(l => l.To == id).Select(l => new CallRow(l.From, Label(l.From), l)));
        Lists.Sync(
            Databases,
            id is null || s is null
                ? []
                : DatabasesOf(s, id).Select(d =>
                {
                    var link = s.Links.FirstOrDefault(x => x.From == id && x.To == d.Id);
                    var detail = d.Detail is "per tenant" ? "one per tenant" : d.Detail;
                    return new DatabaseRow(d.Name, detail, link is { What.Count: > 0 } ? CallRow.Cut(link.What, 3) : null);
                })
        );
        foreach (
            var name in new[]
            {
                nameof(HasSelection), nameof(NoSelection), nameof(SelectedNode), nameof(SelectedName), nameof(SelectedKind), nameof(SelectedHref),
                nameof(HasSelectedHref), nameof(CallsHead), nameof(CalledByHead), nameof(DatabasesHead), nameof(HasCalls), nameof(HasCalledBy),
                nameof(HasDatabases),
            }
        )
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>A member the focused one uses or is used by: its name, the packages and the manifests naming them.</summary>
public sealed record DepLink(string Id, string Name, string? Packages, string? Via, bool Indirect)
{
    public bool HasPackages => Packages is not null;
    public bool HasVia => Via is not null;
}

/// <summary>A chip above the map: a foundation (used by most), or a cycle; selecting it focuses its member.</summary>
public sealed record DepChip(string Id, string Text);

/// <summary>
/// Build: packages (app.js dependencyMap): which members compile against another member's packages, read from their
/// manifests; the map with focus, a filter and indirect reach, the focused member beside it, the foundations and
/// cycles above it.
/// </summary>
public sealed partial class BuildSection : SectionViewModel
{
    private readonly WorkspaceContext _context;
    private DependencyModel? _model;

    public BuildSection(WorkspaceContext context)
        : base("Build dependencies", null, ["sync", "findings", "levels"])
    {
        _context = context;
    }

    public ObservableCollection<DepChip> Foundations { get; } = [];
    public ObservableCollection<DepChip> Cycles { get; } = [];
    public ObservableCollection<DepLink> Uses { get; } = [];
    public ObservableCollection<DepLink> UsedBy { get; } = [];

    [ObservableProperty]
    public partial DependencyModel? Model { get; private set; }

    [ObservableProperty]
    public partial string? Focused { get; set; }

    [ObservableProperty]
    public partial bool Indirect { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial string? Empty { get; private set; }

    [ObservableProperty]
    public partial string MapHint { get; private set; } = "";

    public bool HasMap => Empty is null && Model is not null;
    public bool HasEmpty => Empty is not null;
    public bool HasFoundations => Foundations.Count > 0;
    public bool HasCycles => Cycles.Count > 0;
    public bool HasFocus => Focused is not null;
    public bool NoFocus => Focused is null;
    public string FocusName => Focused is { } f ? Label(f) : "";
    public string? FocusHref => Focused is { } f ? Route.ProjectHref(f) : null;
    public string? CycleNote { get; private set; }
    public bool HasCycleNote => CycleNote is not null;
    public string UsesHead => $"Uses {Uses.Count}";
    public string UsesNote => Indirect ? " · their packages it references, directly or through others" : " · their packages it references";
    public string UsedByHead => $"Used by {UsedBy.Count}";
    public bool HasCyclesHint => _model?.Cycles.Count > 0;

    partial void OnFocusedChanged(string? value) => DrawSide();

    partial void OnIndirectChanged(bool value) => DrawSide();

    [RelayCommand]
    private void Pick(string id) => Focused = Focused == id ? null : id;

    [RelayCommand]
    private void Clear() => Focused = null;

    private string Label(string id) => _context.Projects.FirstOrDefault(p => p.Id == id)?.DisplayName ?? _model?.Label(id) ?? id;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var g = await _context.Services.Api.DependenciesAsync(_context.Name, token);
        var model = new DependencyModel(
            [.. g.Nodes.Select(n => new DepNode(n.ProjectId, _context.Projects.FirstOrDefault(p => p.Id == n.ProjectId)?.DisplayName ?? n.Project, n.Library))],
            [.. g.Edges.Select(e => new DepEdge(e.From, e.To, e.Packages, Via(e)))]
        );
        _model = model;
        Empty = null;
        if (model.Nodes.Count is 0)
        {
            Empty = "No member could be read.";
        }
        else if (model.Edges.Count is 0)
        {
            Empty = "No member references another member's packages.";
        }
        MapHint =
            "Which members compile against another member's packages (NuGet, npm, Go modules), read from their manifests: what has to be upgraded and rebuilt when one changes. "
            + (model.Nodes.Count <= 12
                ? "Read it on two axes: down is depth (users above what they use, as in Runtime), right is how many members use it. A member three or more others use gets a bus: where their lines run together, they draw as one heavier line. A library on a bus stands at the right, level with the middle of its users, whose lines meet and run in together."
                : "Users above what they use, as in Runtime; select a member to follow its lines.")
            + " Libraries (no host or app of their own) are dashed.";
        Lists.Sync(Foundations, model.Foundation.OrderByDescending(id => model.UsedBy[id].Count).Select(id => new DepChip(id, $"{Label(id)}  used by {model.UsedBy[id].Count}")));
        Lists.Sync(Cycles, model.Cycles.Select(c => new DepChip(c[0], "▲ " + string.Join(" ⇄ ", c.Select(Label).Order(StringComparer.OrdinalIgnoreCase)))));
        Model = model;
        foreach (var name in new[] { nameof(HasMap), nameof(HasEmpty), nameof(HasFoundations), nameof(HasCycles), nameof(HasCyclesHint) })
        {
            OnPropertyChanged(name);
        }
        DrawSide();
    }

    /// <summary>The using member's projects (manifests) naming the packages, without the prefix their names share.</summary>
    private static string Via(DependencyEdge e)
    {
        var names = e.Via.Select(r => System.IO.Path.GetFileName(r.Path))
            .Select(n => System.IO.Path.GetExtension(n) is ".csproj" or ".json" or ".mod" ? System.IO.Path.GetFileNameWithoutExtension(n) : n)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var parts = names.Select(n => n.Split('.')).ToArray();
        var k = 0;
        while (parts.Length > 1 && parts.All(q => q.Length > k + 1 && q[k] == parts[0][k]))
        {
            k++;
        }
        return string.Join(", ", parts.Select(q => string.Join('.', q.Skip(k))));
    }

    private void DrawSide()
    {
        var m = _model;
        var f = Focused;
        CycleNote = null;
        if (m is null || f is null || !m.Uses.ContainsKey(f))
        {
            Uses.Clear();
            UsedBy.Clear();
        }
        else
        {
            var uses = m.Reach(f, true, Indirect);
            var by = m.Reach(f, false, Indirect);
            DepLink Link(string x, string from, string to)
            {
                var e = m.Edges.FirstOrDefault(d => d.From == from && d.To == to);
                if (e is null)
                {
                    return new DepLink(x, Label(x), null, null, true);
                }
                var via = e.Via.Length > 0 ? "in " + e.Via : null;
                return new DepLink(x, Label(x), CallRow.Cut(e.Packages, 2), via, false);
            }
            Lists.Sync(Uses, uses.OrderBy(Label, StringComparer.OrdinalIgnoreCase).Select(x => Link(x, f, x)));
            Lists.Sync(UsedBy, by.OrderBy(Label, StringComparer.OrdinalIgnoreCase).Select(x => Link(x, x, f)));
            if (m.CycleOf(f) is { } cycle)
            {
                var partners = cycle.Where(x => x != f).Select(Label).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                CycleNote =
                    $"In a cycle: {Label(f)} uses {string.Join(", ", partners)} and {(partners.Length > 1 ? "they use" : "it uses")} {Label(f)}, so none of them can change or ship alone.";
            }
        }
        foreach (
            var name in new[]
            {
                nameof(HasFocus), nameof(NoFocus), nameof(FocusName), nameof(FocusHref), nameof(CycleNote), nameof(HasCycleNote), nameof(UsesHead),
                nameof(UsesNote), nameof(UsedByHead),
            }
        )
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>A library in the list (app.js liblist): a member whose packages others use, and how many do.</summary>
public sealed partial class LibraryChoice(DependencyNode node, string label, int users) : ObservableObject
{
    public DependencyNode Node { get; } = node;
    public string Label { get; } = label;
    public string Count => $"used by {users}";

    [ObservableProperty]
    public partial bool Selected { get; set; }
}

/// <summary>A package in the library's matrix: who references it, and how many of the other members.</summary>
public sealed record PackageUseRow(string Name, IReadOnlyList<string> Cells, string UsedBy, bool Unused);

/// <summary>
/// Libraries (app.js sharedCode, libraryView): members whose packages other members use, and how much of each is used:
/// a matrix of the library's packages by the members using any, how many code files import it, their own copies of
/// its types.
/// </summary>
public sealed partial class LibrariesSection(WorkspaceContext context) : SectionViewModel("", null, ["sync", "findings", "levels"])
{
    private int _total;
    private int _load;

    public ObservableCollection<LibraryChoice> Libraries { get; } = [];
    public ObservableCollection<string> Columns { get; } = [];
    public ObservableCollection<PackageUseRow> Packages { get; } = [];
    public ObservableCollection<string> Imports { get; } = [];
    public ObservableCollection<string> Copies { get; } = [];

    [ObservableProperty]
    public partial LibraryChoice? Library { get; private set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial string? NotUsing { get; private set; }

    [ObservableProperty]
    public partial string? DetailMessage { get; private set; }

    public bool IsEmpty => Libraries.Count is 0;
    public bool HasDetail => Summary is not null;
    public bool HasNotUsing => NotUsing is not null;
    public bool HasDetailMessage => DetailMessage is not null;
    public string SharedHref => Route.WorkspaceHref(context.Name, "issues", "shared");

    [RelayCommand]
    private Task PickAsync(LibraryChoice choice)
    {
        foreach (var l in Libraries)
        {
            l.Selected = l == choice;
        }
        Library = choice;
        return AdoptionAsync(choice);
    }

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var g = await context.Services.Api.DependenciesAsync(context.Name, token);
        int Users(string id) => g.Edges.Count(e => e.To == id);
        _total = g.Nodes.Count;
        var libs = g.Nodes.Where(n => n.Packages.Count > 0 && Users(n.ProjectId) > 0)
            .OrderByDescending(n => Users(n.ProjectId))
            .ThenBy(n => n.Project, StringComparer.OrdinalIgnoreCase)
            .Select(n => new LibraryChoice(n, context.Projects.FirstOrDefault(p => p.Id == n.ProjectId)?.DisplayName ?? n.Project, Users(n.ProjectId)))
            .ToArray();
        var keep = Library?.Node.Project;
        Lists.Sync(Libraries, libs);
        OnPropertyChanged(nameof(IsEmpty));
        if (libs.Length > 0)
        {
            await PickAsync(libs.FirstOrDefault(l => l.Node.Project == keep) ?? libs[0]);
        }
    }

    private async Task AdoptionAsync(LibraryChoice choice)
    {
        var load = ++_load;
        DetailMessage = "Reading every member's files…";
        OnPropertyChanged(nameof(HasDetailMessage));
        try
        {
            var r = await context.Services.Api.AdoptionAsync(context.Name, choice.Node.Project, CancellationToken.None);
            if (load != _load)
            {
                return;
            }
            Draw(r);
            DetailMessage = null;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            if (load == _load)
            {
                DetailMessage = exception.Message;
                Summary = null;
            }
        }
        OnPropertyChanged(nameof(HasDetailMessage));
        OnPropertyChanged(nameof(HasDetail));
    }

    private void Draw(LibraryAdoption r)
    {
        var others = _total - 1;
        var consumers = r.Consumers.OrderByDescending(c => c.Packages?.Count ?? 0).ThenByDescending(c => c.ImportFiles).ToArray();
        var users = r.Packages.ToDictionary(p => p, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var c in consumers)
        {
            foreach (var matched in (c.Packages ?? []).Select(p => p.Matched).OfType<string>())
            {
                if (!users.TryGetValue(matched, out var set))
                {
                    users[matched] = set = new HashSet<string>(StringComparer.Ordinal);
                }
                set.Add(c.Project);
            }
        }
        var used = r.Packages.Count(p => users[p].Count > 0);
        Summary =
            $"Offers {Shell.Plural(r.Packages.Count, "package")}; {consumers.Length} of {Shell.Plural(others, "other project")} use it"
            + (used < r.Packages.Count ? $", {Shell.Plural(r.Packages.Count - used, "package")} nobody here uses." : ".");
        Lists.Sync(Columns, consumers.Select(c => context.Folder(c.Project)));
        Lists.Sync(
            Packages,
            r.Packages.OrderByDescending(p => users[p].Count)
                .ThenBy(p => p, StringComparer.Ordinal)
                .Select(p =>
                    new PackageUseRow(
                        p,
                        [.. consumers.Select(c => users[p].Contains(c.Project) ? "✓" : "·")],
                        users[p].Count > 0 ? $"{users[p].Count} of {others}" : "nobody",
                        users[p].Count is 0
                    )
                )
        );
        Lists.Sync(Imports, consumers.Select(c => c.ImportFiles.ToString(CultureInfo.InvariantCulture)));
        var twins = r.PossibleLocalImplementations.GroupBy(t => t.Project).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Lists.Sync(Copies, consumers.Select(c => twins.TryGetValue(c.Project, out var n) ? $"▲ {n}" : "none"));
        NotUsing = r.NonConsumers.Count > 0 ? $"Not using it: {string.Join(", ", r.NonConsumers)}." : null;
        OnPropertyChanged(nameof(HasNotUsing));
    }
}

/// <summary>
/// A workspace's Architecture (app.js workspaceView "architecture"): Runtime: calls, Build: packages, or Libraries, by
/// the address; each part loads once it is shown.
/// </summary>
public sealed partial class WorkspaceArchitectureViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceContext _context;
    private readonly HashSet<string> _started = new(StringComparer.Ordinal);

    public WorkspaceArchitectureViewModel(WorkspaceContext context, Route route)
    {
        _context = context;
        System = new SystemSection(context);
        Build = new BuildSection(context);
        Libraries = new LibrariesSection(context);
        Show(route);
    }

    public SystemSection System { get; }
    public BuildSection Build { get; }
    public LibrariesSection Libraries { get; }
    public ObservableCollection<TabItem> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRuntime), nameof(ShowsBuild), nameof(ShowsLibraries))]
    public partial string Lens { get; private set; } = "runtime";

    public bool ShowsRuntime => Lens is "runtime";
    public bool ShowsBuild => Lens is "build";
    public bool ShowsLibraries => Lens is "libraries";

    public void Show(Route route)
    {
        Lens = route.Item is "build" or "libraries" ? route.Item : "runtime";
        Lists.Sync(
            Sections,
            new[] { ("runtime", "Runtime: calls"), ("build", "Build: packages"), ("libraries", "Libraries") }.Select(s =>
                new TabItem(s.Item1, s.Item2, Route.WorkspaceHref(_context.Name, "architecture", s.Item1 is "runtime" ? null : s.Item1), 0, s.Item1 == Lens)
            )
        );
        if (_started.Add(Lens))
        {
            SectionViewModel section = Lens switch
            {
                "build" => Build,
                "libraries" => Libraries,
                _ => System,
            };
            section.Follow(_context.Services.Live);
            _ = section.LoadAsync();
        }
    }

    public void Dispose()
    {
        System.Dispose();
        Build.Dispose();
        Libraries.Dispose();
    }
}
