using System.Collections.ObjectModel;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>This page's settings kept for its user: the theme and the view a project opens on (app.js store).</summary>
public static class PageSettings
{
    public static readonly IReadOnlyList<Choice> Themes = [new("system", "System"), new("light", "Light"), new("dark", "Dark")];

    /// <summary>Applies the stored theme to the application (System follows the operating system).</summary>
    public static void ApplyTheme(IPreferences preferences)
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = preferences.Get("theme") switch
            {
                "light" => ThemeVariant.Light,
                "dark" => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    /// <summary>The view a project opens on from the sidebar (Overview unless chosen).</summary>
    public static string ProjectView(IPreferences preferences) =>
        preferences.Get("projectView") is { Length: > 0 } view && Route.ProjectViews.Any(v => v.View == view) ? view : "overview";
}

/// <summary>An SCM connection as Machine lists it (app.js scmConnections row).</summary>
public sealed partial class ScmRow(ScmEntry entry) : ObservableObject
{
    private static readonly Dictionary<string, string> Failures = new(StringComparer.Ordinal)
    {
        ["unreachable"] = "Unreachable",
        ["tls"] = "TLS failed",
        ["unauthorized"] = "Token rejected",
        ["forbidden"] = "Not permitted",
        ["not_found"] = "Not found",
        ["wrong_server"] = "Wrong server type",
        ["invalid"] = "Refused",
        ["rate_limited"] = "Rate limited",
        ["server"] = "Server error",
    };

    public ScmEntry Entry { get; } = entry;
    public string TypeLabel => ScmTypes.Label(Entry.Type);
    public bool HasAliases => Entry.Aliases.Count > 0;
    public string Aliases => "Also cloned from " + string.Join(", ", Entry.Aliases);

    /// <summary>Where its token comes from and whether it is there, never the token itself.</summary>
    public string TokenLine
    {
        get
        {
            if (Entry.TokenSource is "env")
            {
                return $"token from ${Entry.TokenEnv}" + (Entry.TokenAvailable ? $" ({Entry.Token})" : " (not set)");
            }
            if (Entry.TokenError is { Length: > 0 })
            {
                return "token cannot be decrypted: store it again";
            }
            return $"token {Entry.Token}" + (Entry.TokenEncrypted is false ? " (unencrypted)" : "");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestLine), nameof(TestOk), nameof(TestFailed), nameof(TestHint), nameof(HasTestHint))]
    public partial ScmTest? Test { get; set; } = entry.LastTest;

    [ObservableProperty]
    public partial bool Testing { get; set; }

    public bool TestOk => Test?.Ok is true;
    public bool TestFailed => Test is { Ok: false };

    public string TestLine
    {
        get
        {
            if (Testing)
            {
                return "Testing…";
            }
            if (Test is not { } t)
            {
                return "Not tested yet.";
            }
            if (t.Ok)
            {
                return "Connected" + (t.Version is { Length: > 0 } v ? $" to version {v}" : "") + (t.User is { Length: > 0 } u ? $" as {u}" : "") + ".";
            }
            var failure = t.Failure is { } f ? Failures.GetValueOrDefault(f, "Failed") : "Failed";
            return t.Message is { Length: > 0 } m ? $"{failure}: {m}" : failure;
        }
    }

    public string? TestHint => Test is { Ok: false } t ? t.Hint : null;
    public bool HasTestHint => TestHint is { Length: > 0 };

    partial void OnTestingChanged(bool value) => OnPropertyChanged(nameof(TestLine));
}

/// <summary>The SCM server types and their names.</summary>
public static class ScmTypes
{
    public static readonly IReadOnlyList<Choice> All = [new("gitlab", "GitLab"), new("forgejo", "Forgejo"), new("github", "GitHub")];

    public static string Label(string type) => All.FirstOrDefault(c => c.Value == type)?.Label ?? type;
}

/// <summary>A graphify-csharp session (app.js graphifySessions row): its solution, state, detail and actions.</summary>
public sealed record GraphifyRow(GraphifySession Session)
{
    public string Solution => Session.ProjectName is { Length: > 0 } name ? name : Path.GetFileName(Session.Root.TrimEnd('\\', '/'));
    public string Href => Session.ProjectId is { } id ? Route.ProjectHref(id, "agents", "tools") : "";
    public bool HasProject => Session.ProjectId is not null;
    public string StateWord => (Session.Summary ?? Session.State ?? "").Split(", ")[0];
    public string Detail => string.Join(" · ", (Session.Summary ?? "").Split(", ").Skip(1));
    public string ShortId => Session.Id[..Math.Min(8, Session.Id.Length)];
    public bool Ready => StateWord is "ready";
    public bool Loading => StateWord is "loading";
    public bool Bad => !Ready && !Loading;
    public string Icon
    {
        get
        {
            if (Ready)
            {
                return "st-ok";
            }
            return Loading ? "st-warn" : "st-fail";
        }
    }
}

/// <summary>
/// The Machine page (app.js renderMachine): this page's settings, the Board's defaults, SCM connections, the developer
/// telemetry opt-in, graphify-csharp's sessions, where the machine's state lives, and the build.
/// </summary>
public sealed partial class MachinePage : PageViewModel
{
    private readonly UiServices _services;

    public MachinePage(UiServices services)
    {
        _services = services;
        Info = new MachineState(services);
        Board = new BoardDefaultsSection(services);
        Scm = new ScmSection(services);
        Telemetry = new TelemetrySection(services, Info);
        Graphify = new GraphifySection(services, null);
        Theme = PageSettings.Themes.First(t => t.Value == (services.Host.Preferences.Get("theme") ?? "system"));
        ProjectView = ProjectViews.First(v => v.Value == PageSettings.ProjectView(services.Host.Preferences));
        foreach (var section in new SectionViewModel[] { Info, Board, Scm, Graphify })
        {
            section.Follow(services.Live);
            _ = section.LoadAsync();
        }
    }

    public MachineState Info { get; }
    public BoardDefaultsSection Board { get; }
    public ScmSection Scm { get; }
    public TelemetrySection Telemetry { get; }
    public GraphifySection Graphify { get; }
    public static IReadOnlyList<Choice> Themes => PageSettings.Themes;
    public IReadOnlyList<Choice> ProjectViews { get; } = [.. Route.ProjectViews.Select(v => new Choice(v.View, v.Label))];

    [ObservableProperty]
    public partial Choice Theme { get; set; }

    [ObservableProperty]
    public partial Choice ProjectView { get; set; }

    partial void OnThemeChanged(Choice value)
    {
        _services.Host.Preferences.Set("theme", value.Value);
        PageSettings.ApplyTheme(_services.Host.Preferences);
    }

    partial void OnProjectViewChanged(Choice value)
    {
        _services.Host.Preferences.Set("projectView", value.Value);
        Route.DefaultProjectView = value.Value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Info.Dispose();
            Board.Dispose();
            Scm.Dispose();
            Graphify.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Where the machine's state lives and the build (GET /api/machine); Telemetry shows its opt-in.</summary>
public sealed partial class MachineState(UiServices services) : SectionViewModel("State", null, ["registry", "workspaces"])
{
    [ObservableProperty]
    public partial MachineInfo? Machine { get; private set; }

    public ObservableCollection<Fact> Paths { get; } = [];

    [ObservableProperty]
    public partial string BuildLine { get; private set; } = "";

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var m = await services.Api.MachineAsync(token);
        Machine = m;
        var global = m.Paths.Global;
        string Inside(string? path) =>
            path is { Length: > 0 } p && p.StartsWith(global, StringComparison.Ordinal) && p.Length > global.Length ? p[(global.Length + 1)..] : path ?? "";
        Lists.Sync(
            Paths,
            new[]
            {
                new Fact("Global folder", global),
                new Fact("Project registry", Inside(m.Paths.Registry)),
                new Fact("Workspaces", Inside(m.Paths.Workspaces)),
                new Fact("Workspace and user knowledge", Inside(m.Paths.Knowledge)),
            }.Where(f => f.Value.Length > 0)
        );
        var b = m.Build;
        BuildLine =
            $"harness {b.Version}"
            + (b.Commit is { Length: >= 12 } c ? $" · {c[..12]}" : "")
            + (b.Date is { Length: >= 10 } d ? $" · built {d[..10]}" : "")
            + (b.Platform is { Length: > 0 } p ? $" · {p}" : "")
            + (b.Runtime is { Length: > 0 } r ? $" · {r}" : "");
    }
}

/// <summary>The Board's defaults for projects without their own (app.js boardDefaults): sort and done-age.</summary>
public sealed partial class BoardDefaultsSection(UiServices services) : SectionViewModel("Board", null, [])
{
    private bool _applying;

    public IReadOnlyList<Choice> Sorts { get; } =
        [new("priority", "Priority"), new("id", "Id"), new("name", "Name"), new("created", "Created"), new("changed", "Last changed")];

    public IReadOnlyList<Choice> Ages { get; } = [.. new[] { 1, 3, 7, 14 }.Select(n => new Choice(n.ToString(System.Globalization.CultureInfo.InvariantCulture), Shell.Plural(n, "day")))];

    [ObservableProperty]
    public partial Choice? Sort { get; set; }

    [ObservableProperty]
    public partial Choice? Age { get; set; }

    protected override async Task LoadCoreAsync(CancellationToken token) => Show((await services.Api.BoardDefaultsAsync(token)).Defaults);

    partial void OnSortChanged(Choice? value)
    {
        if (!_applying && value is not null)
        {
            _ = SaveAsync(new BoardDefaultsChange { Sort = value.Value });
        }
    }

    partial void OnAgeChanged(Choice? value)
    {
        if (!_applying && value is not null)
        {
            _ = SaveAsync(new BoardDefaultsChange { HideDoneDays = int.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private void Show(BoardDefaults defaults)
    {
        _applying = true;
        Sort = Sorts.FirstOrDefault(c => c.Value == defaults.Sort);
        Age = Ages.FirstOrDefault(c => c.Value == defaults.HideDoneDays.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _applying = false;
    }

    private async Task SaveAsync(BoardDefaultsChange change)
    {
        try
        {
            Show(await services.Api.SetBoardDefaultsAsync(change, CancellationToken.None));
            services.Toasts.Show("Board default saved");
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }
}

/// <summary>This machine's SCM connections (app.js scmConnections): each with its test, edit and remove; Add.</summary>
public sealed partial class ScmSection(UiServices services) : SectionViewModel("SCM connections", null, [])
{
    public ObservableCollection<ScmRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string? File { get; private set; }

    public bool IsEmpty => Rows.Count is 0;

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var list = await services.Api.ScmAsync(token);
        Rows.Clear();
        foreach (var entry in list.Scm)
        {
            Rows.Add(new ScmRow(entry));
        }
        File = list.File;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task TestAsync(ScmRow row)
    {
        row.Testing = true;
        try
        {
            row.Test = await services.Api.TestScmAsync(new ScmForm { Name = row.Entry.Name, Type = "", Address = "" }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            row.Test = new ScmTest { Ok = false, Message = exception.Message };
        }
        finally
        {
            row.Testing = false;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (await services.Dialogs.ShowAsync(ScmFormDialog.For(services, null)))
        {
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task EditAsync(ScmRow row)
    {
        if (await services.Dialogs.ShowAsync(ScmFormDialog.For(services, row.Entry)))
        {
            await RefreshAsync(force: true);
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(ScmRow row)
    {
        var confirm = new ConfirmDialog
        {
            Title = $"Remove SCM {row.Entry.Name}?",
            Sub = row.Entry.Address,
            Message = "• Its token is deleted from this machine.\n• Projects on this server lose the merge request tools until it is added again.",
            AcceptLabel = "Remove",
            Danger = true,
        };
        if (!await services.Dialogs.ConfirmAsync(confirm))
        {
            return;
        }
        try
        {
            await services.Api.RemoveScmAsync(row.Entry.Name, CancellationToken.None);
            services.Toasts.Show($"Removed SCM {row.Entry.Name}");
            await RefreshAsync(force: true);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }
}

/// <summary>
/// Adding an SCM or editing one (app.js scmForm): name, type, URL, token (empty keeps the stored one on an edit) and
/// aliases; Test connection checks the values as typed before they are saved.
/// </summary>
public sealed partial class ScmFormDialog : FormDialog<bool>
{
    private readonly UiServices _services;
    private readonly ScmEntry? _entry;

    private ScmFormDialog(UiServices services, ScmEntry? entry)
    {
        _services = services;
        _entry = entry;
        Name = entry?.Name ?? "";
        Type = entry is null ? null : ScmTypes.All.FirstOrDefault(c => c.Value == entry.Type);
        Address = entry?.Address ?? "";
        Aliases = [.. (entry?.Aliases ?? []).Select(a => new LineItem(a))];
    }

    public static ScmFormDialog For(UiServices services, ScmEntry? entry) =>
        new(services, entry)
        {
            Title = entry is null ? "Add an SCM" : $"Edit SCM {entry.Name}",
            Sub = "GitLab (API v4), Forgejo (API v1) or GitHub (REST API), authenticated with an access token.",
            Wide = true,
            AcceptLabel = entry is null ? "Add SCM" : "Save",
        };

    public static IReadOnlyList<Choice> Types => ScmTypes.All;
    public bool Editing => _entry is not null;
    public string TokenHint => Editing ? "Leave empty to keep the stored token." : "A personal or project access token with API access (GitLab: api; GitHub: pull requests read and write).";
    public string TokenPlaceholder => Editing ? "(unchanged)" : "";
    public ObservableCollection<LineItem> Aliases { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Choice? Type { get; set; }

    [ObservableProperty]
    public partial string Address { get; set; }

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    public partial string? TestLine { get; private set; }

    [ObservableProperty]
    public partial bool TestOk { get; private set; }

    [RelayCommand]
    private void AddAlias() => Aliases.Add(new LineItem(""));

    [RelayCommand]
    private void RemoveAlias(LineItem alias) => Aliases.Remove(alias);

    /// <summary>Tests the values as typed (a saved one's own token when the field is left empty).</summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        TestLine = "Testing…";
        Error = null;
        try
        {
            var test = await _services.Api.TestScmAsync(Form(), CancellationToken.None);
            TestOk = test.Ok;
            TestLine = new ScmRow(new ScmEntry { Name = Name, Type = Type?.Value ?? "", Address = Address, LastTest = test }).TestLine;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            TestLine = null;
            Error = exception.Message;
        }
    }

    protected override string? Validate()
    {
        if (Name.Trim().Length is 0)
        {
            return "Name: how projects and the CLI refer to it.";
        }
        if (Type is null)
        {
            return "Type: choose GitLab, Forgejo or GitHub.";
        }
        return Address.Trim().Length is 0 ? "URL: the server's base address." : null;
    }

    protected override async Task<bool> SendAsync(CancellationToken token)
    {
        var form = Form();
        await _services.Api.SaveScmAsync(
            _entry?.Name,
            new ScmForm
            {
                Name = form.Name,
                Type = form.Type,
                Address = form.Address,
                Token = form.Token,
                Aliases = [.. Aliases.Select(a => a.Text.Trim()).Where(a => a.Length > 0)],
            },
            token
        );
        _services.Toasts.Show(_entry is null ? $"SCM {Name.Trim()} added" : $"SCM {Name.Trim()} saved");
        return true;
    }

    private ScmForm Form() => new() { Name = _entry?.Name ?? Name.Trim(), Type = Type?.Value ?? "", Address = Address.Trim(), Token = Token.Trim() };
}

/// <summary>The developer telemetry opt-in as a switch (app.js telemetryCard): on asks for the collector's endpoint.</summary>
public sealed partial class TelemetrySection : ObservableObject
{
    private readonly UiServices _services;
    private readonly MachineState _machine;

    public TelemetrySection(UiServices services, MachineState machine)
    {
        _services = services;
        _machine = machine;
        // the opt-in shown is the server's once the machine's state is read
        machine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MachineState.Machine))
            {
                Notify();
            }
        };
    }

    [ObservableProperty]
    public partial bool Editing { get; private set; }

    [ObservableProperty]
    public partial string Endpoint { get; set; } = "";

    [ObservableProperty]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial TelemetryState? Saved { get; private set; }

    private TelemetryState? State => Saved ?? _machine.Machine?.Telemetry;
    public bool Enabled => State?.Enabled is true;
    public string Heading => Enabled ? "Sharing" : "Not sharing";

    public string Note
    {
        get
        {
            if (!Enabled)
            {
                return "Turn on to send a daily report to a harness-collect server.";
            }
            var sent = State!.LastSent is { Length: > 0 } last ? "last sent " + Shell.Ago(last) : "nothing sent yet";
            return $"To {State.Endpoint}; {sent}, at most daily after a sync.";
        }
    }

    public string SaveLabel => Enabled ? "Save" : "Turn on";
    public string TokenPlaceholder => State?.Endpoint is { Length: > 0 } ? "(unchanged)" : "if the collector asks for one";
    public bool CanChange => Enabled && !Editing;

    /// <summary>The switch: on opens the form, off stops sending.</summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Enabled)
        {
            await SendAsync(new TelemetryChange { Enabled = false });
        }
        else
        {
            Edit();
        }
    }

    [RelayCommand]
    private void Edit()
    {
        Endpoint = State?.Endpoint ?? "";
        Token = "";
        Error = null;
        Editing = true;
        Notify();
    }

    [RelayCommand]
    private void Cancel()
    {
        Editing = false;
        Notify();
    }

    [RelayCommand]
    private Task SaveAsync() => SendAsync(new TelemetryChange { Enabled = true, Endpoint = Endpoint.Trim(), Token = Token });

    private async Task SendAsync(TelemetryChange change)
    {
        try
        {
            Saved = await _services.Api.SetTelemetryAsync(change, CancellationToken.None);
            Editing = false;
            Error = null;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        Notify();
    }

    private void Notify()
    {
        foreach (var name in new[] { nameof(Enabled), nameof(Heading), nameof(Note), nameof(SaveLabel), nameof(TokenPlaceholder), nameof(CanChange) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>graphify-csharp's warm sessions (app.js graphifySessions), the machine's or one project's.</summary>
public sealed partial class GraphifySection(UiServices services, string? projectId) : SectionViewModel("C# sessions (graphify-csharp)", projectId, [])
{
    public ObservableCollection<GraphifyRow> Rows { get; } = [];

    [ObservableProperty]
    public partial string? ServerError { get; private set; }

    public bool IsEmpty => Rows.Count is 0;
    public bool ForProject => ProjectId is not null;

    public string EmptyText =>
        ForProject
            ? "No warm session for this project: the first callers, references or impact question starts one."
            : "No graphify sessions on this machine.";

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var answer = await services.Api.GraphifySessionsAsync(ProjectId, token);
        Lists.Sync(Rows, answer.Sessions.Select(s => new GraphifyRow(s)));
        ServerError = answer.Error;
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task RefreshNowAsync() => RefreshAsync(force: true);

    [RelayCommand]
    private Task RestartAsync(GraphifyRow row) => ActAsync("restart", new GraphifyAction { Project = row.Session.ProjectId });

    [RelayCommand]
    private Task StopAsync(GraphifyRow row) => ActAsync("stop", new GraphifyAction { Session = row.Session.Id });

    [RelayCommand]
    private Task StartAsync() => ActAsync("start", new GraphifyAction { Project = ProjectId });

    private async Task ActAsync(string action, GraphifyAction body)
    {
        try
        {
            await services.Api.GraphifyAsync(action, body, CancellationToken.None);
            // a session takes a moment to change state
            await Task.Delay(services.Host.SessionSettle);
            await RefreshAsync(force: true);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            services.Toasts.Fail(exception.Message);
        }
    }
}

/// <summary>A kind of search result, its words, what it is searched by, and whether it is shown.</summary>
public sealed partial class SearchKind(string kind, string label, string hint, Action changed) : ObservableObject
{
    public string Kind { get; } = kind;
    public string Label { get; } = label;
    public string Hint { get; } = hint;

    [ObservableProperty]
    public partial int Count { get; set; }

    [ObservableProperty]
    public partial bool On { get; set; } = true;

    partial void OnOnChanged(bool value) => changed();
}

/// <summary>A search result (app.js hitRow): what it is, where it lives, and the page it leads to.</summary>
public sealed record HitRow(SearchHit Hit)
{
    public string Href =>
        Hit.Kind switch
        {
            "project" => Route.ProjectHref(Hit.ProjectId ?? "", "overview"),
            "workspace" => Route.WorkspaceHref(Hit.Workspace ?? Hit.Title),
            "task" => Route.ProjectHref(Hit.ProjectId ?? "", "work", Hit.Ref),
            "blueprint" => Route.ProjectHref(Hit.ProjectId ?? "", "work"),
            _ when Hit.ProjectId is { } id => Route.ProjectHref(id, "knowledge"),
            _ when Hit.Workspace is { } w => Route.WorkspaceHref(w, "knowledge"),
            _ => "#/knowledge",
        };

    public string Where
    {
        get
        {
            if (Hit.Kind is "project" or "workspace")
            {
                return Hit.Detail ?? "";
            }
            if (Hit.Project is { Length: > 0 } project)
            {
                return project;
            }
            return Hit.Workspace is { Length: > 0 } w ? "workspace · " + w : "you · all your projects";
        }
    }

    public bool HasTag => Hit.Kind is "memory" or "task";
    public string Tag => Hit.Detail ?? "";
    public string? Brush => Hit.Kind is "memory" ? MemoryTypes.Brush(Hit.Detail ?? "") : null;
    public bool HasBrush => Brush is not null;
}

/// <summary>A kind's results.</summary>
public sealed record HitGroup(string Label, string Count, IReadOnlyList<HitRow> Hits);

/// <summary>
/// Search (app.js renderSearch): projects, workspaces, knowledge, tasks and blueprints across the machine, by the
/// address (#/search?q=…); kinds toggle; with fewer than two characters, what each kind is searched by.
/// </summary>
public sealed partial class SearchPage : PageViewModel
{
    private readonly UiServices _services;
    private SearchAnswer? _answer;

    public SearchPage(UiServices services, Route route)
    {
        _services = services;
        Kinds =
        [
            new("project", "Projects", "by name or folder", Draw),
            new("workspace", "Workspaces", "by name", Draw),
            new("memory", "Memories", "decisions, conventions, warnings… by their words", Draw),
            new("task", "Tasks", "by id or title", Draw),
            new("blueprint", "Blueprints", "by id, title or goal", Draw),
        ];
        Show(route);
    }

    public IReadOnlyList<SearchKind> Kinds { get; }
    public ObservableCollection<HitGroup> Groups { get; } = [];
    public IEnumerable<SearchKind> Present => Kinds.Where(k => k.Count > 0);

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Searched { get; private set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string? Nothing { get; private set; }

    [ObservableProperty]
    public partial bool Loading { get; private set; }

    public bool ShowsKinds => Searched.Length < 2;

    /// <summary>The address's query: searched when it has two characters or more.</summary>
    public void Show(Route route)
    {
        Query = route.Query;
        if (route.Query == Searched && _answer is not null)
        {
            return;
        }
        Searched = route.Query;
        OnPropertyChanged(nameof(ShowsKinds));
        Groups.Clear();
        _answer = null;
        if (Searched.Length >= 2)
        {
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private void Submit() => _services.Host.Navigation.Go(new Route { Page = "search", Query = Query.Trim() }.Href);

    private async Task LoadAsync()
    {
        Loading = true;
        try
        {
            _answer = await _services.Api.SearchAsync(Searched, CancellationToken.None);
            Error = null;
            foreach (var kind in Kinds)
            {
                kind.Count = _answer.Hits.Count(h => h.Kind == kind.Kind);
            }
            OnPropertyChanged(nameof(Present));
            Draw();
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        finally
        {
            Loading = false;
        }
    }

    private void Draw()
    {
        if (_answer is not { } answer)
        {
            return;
        }
        var groups = Kinds
            .Where(k => k.On && k.Count > 0)
            .Select(k =>
            {
                var hits = answer.Hits.Where(h => h.Kind == k.Kind).Select(h => new HitRow(h)).ToArray();
                return new HitGroup(k.Label, hits.Length >= answer.PerKind ? $"{hits.Length}+" : hits.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), hits);
            })
            .ToArray();
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(group);
        }
        Nothing = groups.Length is 0 ? $"Nothing matches “{Searched}”" : null;
    }
}

/// <summary>A docs section in the list beside the text.</summary>
public sealed record DocLink(string Slug, string Title, bool Current)
{
    public string Href => "#/docs/" + Uri.EscapeDataString(Slug);
}

/// <summary>A document's sections; its first section is its introduction, opened by the document's heading.</summary>
public sealed record DocGroup(string Title, DocLink? Intro, IReadOnlyList<DocLink> Items)
{
    public bool HasIntro => Intro is not null;
}

/// <summary>
/// Docs (app.js renderDocs): the guide and the reference this harness carries, one section at a time (#/docs/slug),
/// the list beside it, and the sections before and after to read on.
/// </summary>
public sealed partial class DocsPage : PageViewModel
{
    private readonly UiServices _services;
    private DocList? _list;

    public DocsPage(UiServices services, Route route)
    {
        _services = services;
        _ = ShowAsync(route);
    }

    public ObservableCollection<DocGroup> Groups { get; } = [];

    [ObservableProperty]
    public partial DocText? Doc { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial DocLink? Previous { get; private set; }

    [ObservableProperty]
    public partial DocLink? Next { get; private set; }

    public async Task ShowAsync(Route route)
    {
        var slug = route.Item.Length > 0 ? route.Item : "guide";
        try
        {
            var list = _list is null ? _services.Api.DocsAsync(CancellationToken.None) : Task.FromResult(_list);
            var text = _services.Api.DocAsync(slug, CancellationToken.None);
            _list = await list;
            try
            {
                Doc = await text;
                Error = null;
            }
            catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
            {
                Doc = null;
                Error = exception.Message;
            }
            Draw(slug);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
    }

    private void Draw(string slug)
    {
        var list = _list!;
        string Title(string doc) => list.Docs.FirstOrDefault(d => d.Name.Equals(doc, StringComparison.OrdinalIgnoreCase))?.Title ?? doc;
        Lists.Sync(
            Groups,
            list.Sections.GroupBy(s => s.Doc)
                .Select(g =>
                {
                    var items = g.Select(s => new DocLink(s.Slug, s.Title, s.Slug == slug)).ToList();
                    var intro = items.Count > 0 && items[0].Title == Title(g.Key) ? items[0] : null;
                    return new DocGroup(Title(g.Key), intro, [.. items.Where(i => i != intro)]);
                })
        );
        var at = list.Sections.ToList().FindIndex(s => s.Slug == slug);
        Previous = at > 0 ? new DocLink(list.Sections[at - 1].Slug, list.Sections[at - 1].Title, false) : null;
        Next = at >= 0 && at < list.Sections.Count - 1 ? new DocLink(list.Sections[at + 1].Slug, list.Sections[at + 1].Title, false) : null;
    }
}
