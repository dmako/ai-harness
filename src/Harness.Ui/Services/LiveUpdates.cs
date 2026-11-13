using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Harness.Ui.Api;

namespace Harness.Ui.Services;

/// <summary>
/// Something on the page that shows data live changes can make stale (app.js mount()): which project it shows (or
/// every project) and which kinds of its data (tasks, knowledge, proposals, summary, findings, sync, activity,
/// levels, registry, workspaces).
/// </summary>
public interface ILiveSection
{
    /// <summary>The project shown, or null for a section not tied to one.</summary>
    string? ProjectId { get; }

    /// <summary>Whether a change to any project's data of <see cref="Kinds"/> concerns it (machine-wide lists).</summary>
    bool AnyProject => false;

    IReadOnlyList<string> Kinds { get; }

    /// <summary>Loads again; <paramref name="force"/> after the page's own write (a form open in it goes too).</summary>
    Task RefreshAsync(bool force);
}

/// <summary>
/// Live updates (app.js queueLive/applyLive/wants): the /api/events changes are merged, applied once they have been
/// quiet for <see cref="Quiet"/> and at least <see cref="Gap"/> after the previous apply, never while a dialog is open
/// (they wait until it closes), and only to the sections whose project and kinds changed. A dropped stream reconnects
/// and, being blind to what changed meanwhile, refreshes everything.
/// </summary>
public sealed partial class LiveUpdates : ObservableObject, IDisposable
{
    private readonly List<ILiveSection> _sections = [];
    private readonly Func<bool> _paused;
    private Pending? _pending;
    private IDisposable? _timer;
    private DateTime _last = DateTime.MinValue;

    public LiveUpdates(Func<bool>? paused = null, TimeSpan? quiet = null, TimeSpan? gap = null, TimeSpan? reconnect = null)
    {
        _paused = paused ?? (() => false);
        Quiet = quiet ?? TimeSpan.FromMilliseconds(500);
        Gap = gap ?? TimeSpan.FromSeconds(3);
        Reconnect = reconnect ?? TimeSpan.FromSeconds(3);
    }

    public TimeSpan Quiet { get; }
    public TimeSpan Gap { get; }
    public TimeSpan Reconnect { get; }

    /// <summary>connecting, live or offline: what the header's live badge shows.</summary>
    [ObservableProperty]
    public partial string State { get; private set; } = "connecting";

    /// <summary>How many times changes were applied (tests and the spike's measurements count them).</summary>
    public int Applies { get; private set; }

    /// <summary>Raised with each applied change, after the sections were asked to reload (the shell's lists use it).</summary>
    public event EventHandler<LiveAppliedEventArgs>? Applied;

    /// <summary>Follows <paramref name="section"/> until the returned handle is disposed.</summary>
    public IDisposable Register(ILiveSection section)
    {
        _sections.Add(section);
        return new Registration(this, section);
    }

    /// <summary>Takes a change from the stream (or a host): merged into what waits, applied after the quiet time.</summary>
    public void Changed(LiveChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        (_pending ??= new Pending()).Add(change);
        Schedule(Quiet);
    }

    /// <summary>After the page's own write (app.js afterWrite): every section now, open forms included.</summary>
    public void RefreshAll()
    {
        _last = DateTime.UtcNow;
        List<ILiveSection> all = [.. _sections];
        foreach (var section in all)
        {
            _ = section.RefreshAsync(force: true);
        }
        Applied?.Invoke(this, new LiveAppliedEventArgs(new LiveChange { All = true }));
    }

    /// <summary>Follows /api/events until <paramref name="token"/> ends: reconnects after a drop and then refreshes all.</summary>
    public async Task FollowAsync(HarnessApi api, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(api);
        var opened = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                State = "connecting";
                await foreach (
                    var change in api.ChangesAsync(
                        () =>
                        {
                            State = "live";
                            if (opened)
                            {
                                // whatever changed while disconnected
                                Changed(new LiveChange { All = true });
                            }
                            opened = true;
                        },
                        token
                    )
                )
                {
                    Changed(change);
                }
            }
            catch (Exception exception) when (exception is HarnessApiException or HttpRequestException or IOException)
            {
                // offline: shown below, retried after the delay
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            State = "offline";
            try
            {
                await Task.Delay(Reconnect, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _sections.Clear();
    }

    /// <summary>Whether <paramref name="section"/> shows something <paramref name="change"/> changed (app.js wants()).</summary>
    public static bool Wants(ILiveSection section, LiveChange change)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(change);
        var kinds = section.Kinds;
        if (change.All)
        {
            return true;
        }
        if (kinds.Count is 0)
        {
            return false;
        }
        if ((change.Registry && kinds.Contains("registry")) || (change.Workspaces && kinds.Contains("workspaces")))
        {
            return true;
        }
        if (change.Levels.Count > 0 && kinds.Contains("levels"))
        {
            return true;
        }
        IEnumerable<string> ids = [];
        if (section.ProjectId is { } id)
        {
            ids = [id];
        }
        else if (section.AnyProject)
        {
            ids = change.Kinds.Keys;
        }
        return ids.Any(p =>
            change.Kinds.TryGetValue(p, out var changed) && changed.Any(k => k == "all" || kinds.Contains(k))
        );
    }

    private void Schedule(TimeSpan quiet)
    {
        _timer?.Dispose();
        var gap = _last + Gap - DateTime.UtcNow;
        _timer = DispatcherTimer.RunOnce(Apply, quiet > gap ? quiet : gap);
    }

    private void Apply()
    {
        _timer = null;
        if (_pending is null)
        {
            return;
        }
        if (_paused())
        {
            // a form is open: leave it be, and look again shortly
            Schedule(TimeSpan.FromMilliseconds(500));
            return;
        }
        var change = _pending.ToChange();
        _pending = null;
        _last = DateTime.UtcNow;
        Applies++;
        // a copy: a reload may replace sections, which registers and unregisters them
        List<ILiveSection> due = [.. _sections.Where(s => Wants(s, change))];
        foreach (var section in due)
        {
            _ = section.RefreshAsync(force: false);
        }
        Applied?.Invoke(this, new LiveAppliedEventArgs(change));
    }

    /// <summary>Changes merged while waiting to be applied (app.js queueLive).</summary>
    private sealed class Pending
    {
        private readonly Dictionary<string, HashSet<string>> _kinds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _levels = new(StringComparer.Ordinal);
        private bool _registry;
        private bool _workspaces;
        private bool _all;

        public void Add(LiveChange change)
        {
            _registry |= change.Registry;
            _workspaces |= change.Workspaces;
            _all |= change.All;
            _levels.UnionWith(change.Levels);
            foreach (var project in change.Projects.Concat(change.Kinds.Keys).Distinct(StringComparer.Ordinal))
            {
                if (!_kinds.TryGetValue(project, out var set))
                {
                    _kinds[project] = set = new HashSet<string>(StringComparer.Ordinal);
                }
                set.UnionWith(change.Kinds.TryGetValue(project, out var kinds) ? kinds : ["all"]);
            }
        }

        public LiveChange ToChange() =>
            new()
            {
                Registry = _registry,
                Workspaces = _workspaces,
                All = _all,
                Levels = [.. _levels],
                Projects = [.. _kinds.Keys],
                Kinds = _kinds.ToDictionary(k => k.Key, k => (IReadOnlyList<string>)[.. k.Value], StringComparer.Ordinal),
            };
    }

    private sealed class Registration(LiveUpdates owner, ILiveSection section) : IDisposable
    {
        public void Dispose() => owner._sections.Remove(section);
    }
}

/// <summary>The change live updates just applied.</summary>
public sealed class LiveAppliedEventArgs(LiveChange change) : EventArgs
{
    public LiveChange Change { get; } = change;
}
