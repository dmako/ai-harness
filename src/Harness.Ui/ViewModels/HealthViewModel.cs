using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>A check as Health shows it (app.js healthReport check()): its shape, value, detail lines and fix.</summary>
public sealed record CheckItem(
    string Name,
    string Level,
    string Value,
    IReadOnlyList<string> Lines,
    string? Fix,
    bool CanFix,
    bool Previewed,
    string? Tip
)
{
    public string Icon =>
        Level switch
        {
            "warn" => "st-warn",
            "fail" => "st-fail",
            _ => "st-ok",
        };

    public bool IsOk => Level is "ok";
    public bool IsWarn => Level is "warn";
    public bool IsFail => Level is "fail";
    public bool HasValue => Value.Length > 0;
    public bool HasFix => Fix is { Length: > 0 };
    public string FixLine => "Fix: " + Fix;
    public bool HasTip => Tip is { Length: > 0 };

    /// <summary>The level in words for a screen reader (the icon alone is a shape).</summary>
    public string AccessibleName => $"{Name}: {Level}";

    public bool Equals(CheckItem? other) =>
        other is not null
        && Name == other.Name
        && Level == other.Level
        && Value == other.Value
        && Fix == other.Fix
        && CanFix == other.CanFix
        && Previewed == other.Previewed
        && Tip == other.Tip
        && Lines.SequenceEqual(other.Lines);

    public override int GetHashCode() => HashCode.Combine(Name, Level, Value, Fix);
}

/// <summary>The passing checks of one area, folded (Installation, Project, Agents' clients, Code providers).</summary>
public sealed record CheckArea(string Name, IReadOnlyList<CheckItem> Checks)
{
    public string Count => $"✓ {Checks.Count} pass";
    public string Names => string.Join(" · ", Checks.Select(c => c.Name));

    public bool Equals(CheckArea? other) => other is not null && Name == other.Name && Checks.SequenceEqual(other.Checks);

    public override int GetHashCode() => HashCode.Combine(Name, Checks.Count);
}

/// <summary>A label and its value in Paths and identifiers.</summary>
public sealed record Fact(string Label, string Value);

/// <summary>A provider's index freshness over the recent syncs.</summary>
public sealed record Freshness(string Provider, string LastOutcome, bool Failed, string Refreshed, string AtCommit, string LastFailure)
{
    public string RefreshedLine => AtCommit.Length > 0 ? $"refreshed {Refreshed} at {AtCommit}" : "refreshed " + Refreshed;
}

/// <summary>A recent sync in a line.</summary>
public sealed record SyncLine(string When, string Mode, string Status, bool Ok, string Commits, string Delta, string Providers, string? Problem)
{
    public bool HasProblem => Problem is { Length: > 0 };
    public bool Failed => Status is "failed";
    public bool Partial => !Ok && !Failed;
    public string Icon => "st-" + Tone;

    private string Tone
    {
        get
        {
            if (Ok)
            {
                return "ok";
            }
            return Failed ? "fail" : "warn";
        }
    }
    public string Head => $"{When} · {Mode} · {Status} · {Commits} · {Delta}";
}

/// <summary>
/// A project's Health (app.js viewHealth/healthReport): what 'harness doctor' reports, what is wrong first with its fix
/// (run here when harness can, a file-writing fix shown first), the passing checks by area, paths and identifiers,
/// index freshness and recent syncs. Providers are started to check them only on request (Check providers). It reads
/// no project state, so it shows while the state cannot be read.
/// </summary>
public sealed partial class HealthViewModel : SectionViewModel
{
    private static readonly Dictionary<string, string> Areas = new(StringComparer.Ordinal)
    {
        ["binary"] = "Installation",
        ["config"] = "Installation",
        ["permissions"] = "Installation",
        ["git"] = "Project",
        ["project"] = "Project",
        ["storage"] = "Project",
        ["database"] = "Project",
        ["knowledge"] = "Project",
        ["claude-code"] = "Agents' clients",
        ["codex"] = "Agents' clients",
    };

    private static readonly string[] AreaOrder = ["Installation", "Agents' clients", "Project", "Code providers"];

    private readonly UiServices _services;
    private bool _probe;

    public HealthViewModel(UiServices services, string projectId)
        : base("Health", projectId)
    {
        _services = services;
        Hint = "What 'harness doctor' reports. Checking the providers starts each one, so it runs only on request.";
    }

    public ObservableCollection<CheckItem> Failing { get; } = [];
    public ObservableCollection<CheckArea> Passing { get; } = [];
    public ObservableCollection<Fact> Paths { get; } = [];
    public ObservableCollection<Fact> GitFacts { get; } = [];
    public ObservableCollection<Freshness> Fresh { get; } = [];
    public ObservableCollection<SyncLine> Syncs { get; } = [];

    [ObservableProperty]
    public partial bool Healthy { get; private set; }

    /// <summary>ok, warn (healthy, with notes) or fail (needs a fix): the colour of the summary's mark.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneIcon))]
    public partial string Tone { get; private set; } = "ok";

    public string ToneIcon => "st-" + Tone;

    [ObservableProperty]
    public partial string Headline { get; private set; } = "";

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>The index is stale or never synced: Sync now is offered.</summary>
    [ObservableProperty]
    public partial bool CanSync { get; private set; }

    [ObservableProperty]
    public partial bool Probing { get; private set; }

    [ObservableProperty]
    public partial string? GitError { get; private set; }

    [ObservableProperty]
    public partial string ChecksHint { get; private set; } = "";

    public bool HasFailing => Failing.Count > 0;
    public bool HasFresh => Fresh.Count > 0;
    public bool HasSyncs => Syncs.Count > 0;

    /// <summary>Starts each provider to check it (the server allows it only from harness ui's own page).</summary>
    [RelayCommand]
    private async Task ProbeAsync()
    {
        _probe = true;
        Probing = true;
        try
        {
            await RefreshAsync(force: true);
        }
        finally
        {
            Probing = false;
        }
    }

    /// <summary>Runs a check's fix; one that writes a file is shown first, and written only when accepted.</summary>
    [RelayCommand]
    private async Task FixAsync(CheckItem check)
    {
        if (!check.Previewed)
        {
            await RunAsync(new FixRequest { Check = check.Name });
            return;
        }
        FixResult preview;
        try
        {
            preview = await _services.Api.FixAsync(ProjectId!, new FixRequest { Check = check.Name, Preview = true }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
            return;
        }
        if (!preview.Ok)
        {
            _services.Toasts.Fail(preview.Message ?? "Failed", preview.Output);
            return;
        }
        var lines = (preview.Output ?? "").ReplaceLineEndings("\n").Split('\n');
        var dialog = new FixPreviewDialog(lines.FirstOrDefault() ?? "", string.Join("\n", lines.Skip(1).Where(l => !l.StartsWith("Dry run", StringComparison.Ordinal))))
        {
            Title = "Fix " + check.Name,
            Wide = true,
            AcceptLabel = "Write it",
        };
        if (await _services.Dialogs.ShowAsync(dialog))
        {
            await RunAsync(new FixRequest { Check = check.Name });
        }
    }

    /// <summary>Syncs the project now (the index is stale or was never synced).</summary>
    [RelayCommand]
    private Task SyncAsync() => RunAsync(new FixRequest { Sync = true });

    protected override async Task LoadCoreAsync(CancellationToken token)
    {
        var probe = _probe;
        _probe = false;
        Show(await _services.Api.HealthAsync(ProjectId!, probe, token));
    }

    private async Task RunAsync(FixRequest fix)
    {
        try
        {
            var result = await _services.Api.FixAsync(ProjectId!, fix, CancellationToken.None);
            if (result.Ok)
            {
                _services.Toasts.Show(result.Message ?? "Done");
                _services.Live.RefreshAll();
            }
            else
            {
                _services.Toasts.Fail(result.Message ?? "Failed", result.Output);
            }
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            _services.Toasts.Fail(exception.Message);
        }
    }

    private void Show(HealthReport report)
    {
        var checks = report.Checks;
        var status = report.Status;
        var db = status.Database;
        var sync = status.Sync;
        int Count(string level) => checks.Count(c => c.Level == level);
        var issues = new List<string>();
        if (Count("fail") > 0)
        {
            issues.Add(Shell.Plural(Count("fail"), "failing check"));
        }
        if (Count("warn") > 0)
        {
            issues.Add(Shell.Plural(Count("warn"), "warning"));
        }
        if (!db.Exists)
        {
            issues.Add("no database");
        }
        else if (db.SchemaVersion != db.SchemaLatest)
        {
            issues.Add($"schema {db.SchemaVersion} of {db.SchemaLatest}");
        }
        if (db.Error is { Length: > 0 } error)
        {
            issues.Add(error);
        }
        if (sync?.Stale is { Length: > 0 })
        {
            issues.Add("index stale");
        }
        else if (sync?.SyncedAt is null)
        {
            issues.Add("never synced");
        }
        Healthy = report.Healthy;
        if (!report.Healthy)
        {
            Tone = "fail";
            Headline = "Needs a fix";
        }
        else
        {
            Tone = issues.Count > 0 ? "warn" : "ok";
            Headline = issues.Count > 0 ? "Healthy, with notes" : "Healthy";
        }
        var summary = new List<string> { issues.Count > 0 ? string.Join(" · ", issues) : $"{checks.Count} checks pass" };
        if (issues.Count is 0 && db.SchemaVersion is { } schema)
        {
            summary.Add($"schema {schema}");
        }
        if (sync?.SyncedAt is { } synced)
        {
            summary.Add($"synced {Shell.Ago(synced)}");
        }
        summary.Add(report.Probed ? "providers checked" : "providers not checked");
        Summary = string.Join(" · ", summary);
        CanSync = sync?.Stale is { Length: > 0 } || sync?.SyncedAt is null;

        var items = checks.Select(c => Item(c, report)).ToArray();
        Lists.Sync(Failing, items.Where(c => c.Level is not "ok"));
        ChecksHint = Failing.Count > 0 ? "What is wrong first, with its fix; then what passes." : "";
        Lists.Sync(
            Passing,
            AreaOrder
                .Select(area => new CheckArea(area, [.. items.Where(c => c.Level is "ok" && Area(c.Name) == area)]))
                .Where(a => a.Checks.Count > 0)
        );

        Lists.Sync(
            Paths,
            new[]
            {
                new Fact("Project id", status.Project?.Id ?? ""),
                new Fact("Copied from", status.Project?.CopiedFrom ?? ""),
                new Fact("Root", status.ProjectRoot),
                new Fact("State root", status.StateRoot != status.ProjectRoot ? status.StateRoot : ""),
                new Fact("Database", db.Path),
                new Fact("Config, global", status.Config?.Global ?? ""),
                new Fact("Config, project", status.Config?.Project ?? ""),
            }.Where(f => f.Value.Length > 0)
        );
        GitError = status.Git is null ? status.GitError ?? "not a Git repository" : null;
        Lists.Sync(GitFacts, GitFactsOf(status));

        var runs = sync?.Runs ?? [];
        Lists.Sync(Fresh, FreshnessOf(runs));
        Lists.Sync(
            Syncs,
            runs.Select(r => new SyncLine(
                Shell.Ago(r.StartedAt),
                r.Mode,
                r.Status,
                r.Status is "ok",
                r.BaseCommit is { Length: >= 7 } b ? $"{b[..7]} → {Short(r.HeadCommit)}" : Short(r.HeadCommit),
                $"+{r.Added} ~{r.Changed} -{r.Removed}",
                string.Join(" · ", r.Providers.Select(p => $"{p.Name} {p.Status.Replace('_', ' ')}")),
                r.Error is { Length: > 0 } e ? e : r.Reason
            ))
        );
        OnPropertyChanged(nameof(HasFailing));
        OnPropertyChanged(nameof(HasFresh));
        OnPropertyChanged(nameof(HasSyncs));
    }

    private static IEnumerable<Fact> GitFactsOf(HealthStatus status)
    {
        if (status.Git is not { } git)
        {
            return [];
        }
        var commit = git.Commit is { Length: >= 12 } c ? c[..12] : git.Commit;
        return new[]
        {
            new Fact("Branch", git.Detached ? "detached HEAD" : git.Branch ?? ""),
            new Fact("Commit", commit ?? ""),
            new Fact("Worktree", git.LinkedWorktree ? "linked" : "main"),
            new Fact("Worktrees", string.Join("\n", status.Worktrees.Select(w => w.Branch is { } b ? $"{w.Path} ({b})" : w.Path))),
            new Fact("Providers", status.Providers.Count > 0 ? string.Join(", ", status.Providers) : "none enabled"),
        }.Where(f => f.Value.Length > 0);
    }

    private static string Area(string name) => Areas.GetValueOrDefault(name, "Code providers");

    private static string Short(string? commit) => commit is { Length: >= 7 } c ? c[..7] : commit ?? "";

    /// <summary>Per provider over the runs (newest first): its last outcome, last refresh and last failure.</summary>
    private static IEnumerable<Freshness> FreshnessOf(IReadOnlyList<SyncRun> runs)
    {
        var names = runs.SelectMany(r => r.Providers.Select(p => p.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var last = runs.First(r => r.Providers.Any(p => p.Name == name));
            var outcome = last.Providers.First(p => p.Name == name).Status;
            var refreshed = runs.FirstOrDefault(r => r.Providers.Any(p => p.Name == name && p.Status is "refreshed"));
            var failedRun = runs.FirstOrDefault(r => r.Providers.Any(p => p.Name == name && p.Status is "failed"));
            var failure = failedRun?.Providers.First(p => p.Name == name && p.Status is "failed");
            yield return new Freshness(
                name,
                outcome.Replace('_', ' '),
                outcome is "failed",
                refreshed is null ? "not in these syncs" : Shell.Ago(refreshed.StartedAt),
                Short(refreshed?.HeadCommit),
                FailureOf(failedRun, failure)
            );
        }
    }

    private static string FailureOf(SyncRun? run, SyncProvider? failure)
    {
        if (run is null)
        {
            return "none";
        }
        var when = Shell.Ago(run.StartedAt);
        return failure?.Detail is { Length: > 0 } detail ? $"{when}: {detail}" : when;
    }

    /// <summary>A check as shown:a passing one's detail split into its value and lines (app.js checkParts).</summary>
    private static CheckItem Item(HealthCheck c, HealthReport report)
    {
        var detail = c.Detail ?? "";
        string value;
        IReadOnlyList<string> lines;
        string? tip = null;
        if (c.Level is "ok")
        {
            (value, lines) = Parts(c.Name, detail);
        }
        else
        {
            // a failing check's detail without the file it read: that is on hover
            var at = Regex.Match(detail, @"\s*\((?:checked|read|in) ([^)]+)\)", RegexOptions.None, TimeSpan.FromSeconds(1));
            tip = at.Success ? at.Groups[1].Value : null;
            value = "";
            lines = [at.Success ? detail.Replace(at.Value, "", StringComparison.Ordinal) : detail];
        }
        return new CheckItem(
            c.Name,
            c.Level,
            value,
            [.. lines.Where(l => l.Length > 0)],
            c.Fix,
            report.Fixable.Contains(c.Name),
            report.Previewed.Contains(c.Name),
            tip
        );
    }

    /// <summary>How a passing check's detail splits into its value and lines, by check (app.js checkParts).</summary>
    private static readonly Dictionary<string, (string Pattern, Func<GroupCollection, (string, IReadOnlyList<string>)> Split)> Shapes =
        new(StringComparer.Ordinal)
        {
            ["binary"] = (@"^(\S+) \((.*)\)$", Binary),
            ["git"] = (@"^(.*) — (.+) at ([0-9a-f]+)$", g => ($"{g[2].Value} @ {Short(g[3].Value)}", [g[1].Value])),
            ["project"] = (@"^(\S+), (\d+) worktree(?:s|\(s\))?$", g => (Shell.Plural(int.Parse(g[2].Value, System.Globalization.CultureInfo.InvariantCulture), "worktree"), [g[1].Value])),
            ["storage"] = (@"^(.*) is (writable.*)$", g => (g[2].Value, [g[1].Value])),
            ["database"] = (@"^(.*), schema (.+)$", g => ($"schema {g[2].Value}", [g[1].Value])),
            ["permissions"] = (@"is (.+)$", g => (g[1].Value, [])),
        };

    private static (string Value, IReadOnlyList<string> Lines) Parts(string name, string d)
    {
        if (name is "config")
        {
            var parts = d.Split("; ");
            return (string.Join(" + ", parts.Select(p => p.Split(' ')[0])), parts);
        }
        if (Shapes.TryGetValue(name, out var shape))
        {
            var match = Match(d, shape.Pattern);
            return match.Success ? shape.Split(match.Groups) : ("", [d]);
        }
        // a client's install: its scopes, or where it is configured
        if (d.Contains("scope: ", StringComparison.Ordinal))
        {
            var parts = d.Split("; ");
            return (string.Join(" + ", parts.Select(p => p.Split(" scope")[0])) + " scope", parts);
        }
        var configured = Match(d, @"^configured: (.*?)(?: \((.+)\))?$");
        if (configured.Success)
        {
            var how = configured.Groups[2].Success ? "configured · " + configured.Groups[2].Value : "configured";
            return (how, [configured.Groups[1].Value]);
        }
        return ("", [d]);
    }

    private static Match Match(string text, string pattern) =>
        Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>The version (a development build's pseudo-version as its commit), and how it was built.</summary>
    private static (string, IReadOnlyList<string>) Binary(GroupCollection g)
    {
        var version = g[1].Value;
        var how = g[2].Value.Replace(", ", " · ", StringComparison.Ordinal);
        if (version is "dev")
        {
            return ("dev build", [how]);
        }
        var shown = Regex.Replace(version, @"^v0\.0\.0-\d{14}-([0-9a-f]{7})[0-9a-f]*", "$1", RegexOptions.None, TimeSpan.FromSeconds(1));
        return (shown, [how, version]);
    }
}

/// <summary>A fix that writes a file, shown first (app.js previewFix): the change as its dry run shows it.</summary>
public sealed class FixPreviewDialog(string head, string diff) : DialogViewModel<bool>
{
    public string Head { get; } = head;
    public string Diff { get; } = diff;
    public static string Note => "Only this file changes; its other lines and comments stay. To undo, remove the table again.";

    protected override Task AcceptAsync()
    {
        Close(true);
        return Task.CompletedTask;
    }
}
