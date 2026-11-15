using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Harness.Ui.Api;
using Harness.Ui.Services;

namespace Harness.Ui.ViewModels;

/// <summary>The agent start form and the hosts started on one task.</summary>
public sealed partial class AgentSessionsViewModel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly string _project;
    private readonly string _task;
    private readonly CancellationTokenSource _closed = new();
    private readonly CancellationToken _token;
    private IReadOnlyDictionary<string, IReadOnlyList<AgentMode>> _modes =
        new Dictionary<string, IReadOnlyList<AgentMode>>(StringComparer.Ordinal);
    private AgentModels _catalog = new();

    public AgentSessionsViewModel(UiServices services, string project, string task)
    {
        _services = services;
        _token = _closed.Token;
        _project = project;
        _task = task;
        Prompt = $"Work on task {task}: claim it with harness (task_claim), then do it.";
    }

    public ObservableCollection<Choice> Clients { get; } = [new("claude-code", "Claude Code"), new("codex", "Codex")];
    public ObservableCollection<Choice> Models { get; } = [];
    public ObservableCollection<AgentMode> Modes { get; } = [];
    public ObservableCollection<AgentPanel> Hosts { get; } = [];

    [ObservableProperty]
    public partial Choice? Client { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherModelVisible))]
    public partial Choice? Model { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    public partial AgentMode? Mode { get; set; }
    [ObservableProperty]
    public partial string OtherModel { get; set; } = "";
    [ObservableProperty]
    public partial string Prompt { get; set; }
    [ObservableProperty]
    public partial string? Error { get; private set; }
    [ObservableProperty]
    public partial string? ModelNote { get; private set; }
    [ObservableProperty]
    public partial bool FormOpen { get; set; }

    public bool OtherModelVisible => Model?.Value is " other";
    public string ModeDescription => Mode is { } mode ? Describe(mode) : "";
    private static string Describe(AgentMode mode) => (mode.Unsafe ? "Unsafe: " : "") + mode.Description;

    partial void OnClientChanged(Choice? value)
    {
        SetModes();
        SetModels(false);
    }

    partial void OnFormOpenChanged(bool value)
    {
        if (value)
        {
            _ = LoadModelsAsync();
        }
    }

    public void SetSessions(TaskSessions sessions)
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }
        _modes = sessions.PermissionModes;
        Client ??= Clients[0];
        SetModes();
        foreach (var host in sessions.Hosts.Where(host => !Hosts.Any(p => p.Key == host.Key)))
        {
            Hosts.Add(new AgentPanel(_services, _project, _task, host, RefreshAsync));
        }
    }

    private async Task RefreshAsync() => SetSessions(await _services.Api.TaskSessionsAsync(_project, _task, _token));

    private void SetModes()
    {
        var list = Client is { } client && _modes.TryGetValue(client.Value, out var modes) ? modes : [];
        Lists.Sync(Modes, list);
        Mode = list.FirstOrDefault(m => m.Default) ?? (list.Count > 0 ? list[0] : null);
    }

    private void SetModels(bool keep)
    {
        var previous = keep ? Model?.Value : null;
        _catalog.Clients.TryGetValue(Client?.Value ?? "", out var catalog);
        var choices = new List<Choice> { new("", catalog?.Default is { } d ? $"Default ({d.Label})" : "Default") };
        choices.AddRange((catalog?.Models ?? []).Select(m => new Choice(m.Id, m.Label + (m.Source is "seen" ? " (seen)" : ""))));
        choices.Add(new Choice(" other", "Other…"));
        Lists.Sync(Models, choices);
        Model = Models.FirstOrDefault(m => m.Value == previous) ?? Models[0];
        ModelNote = catalog?.Refreshing is true ? "Updating the model list…" : CatalogError(catalog);
    }

    private static string? CatalogError(AgentCatalog? catalog) =>
        catalog?.Error is { Length: > 0 } ? "The model list could not be updated." : null;

    private async Task LoadModelsAsync()
    {
        try
        {
            for (var attempt = 0; attempt < 40 && FormOpen && !_closed.IsCancellationRequested; attempt++)
            {
                _catalog = await _services.Api.AgentModelsAsync(_project, _token);
                SetModels(true);
                if (!_catalog.Clients.Values.Any(c => c.Refreshing))
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(3), _token);
            }
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { /* Dialog closed. */ }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            ModelNote = "The model list is not available.";
        }
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (Client is not { } client)
        {
            return;
        }
        var started = false;
        try
        {
            Error = null;
            await _services.Api.StartAgentAsync(_project, _task, new AgentStart
            {
                Client = client.Value, Prompt = Prompt, Mode = Mode?.Id ?? "",
                Model = OtherModelVisible ? OtherModel.Trim() : Model?.Value ?? "",
            }, _token);
            started = true;
            FormOpen = false;
            SetSessions(await _services.Api.TaskSessionsAsync(_project, _task, _token));
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { /* Dialog closed. */ }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = started ? "Agent started, but its sessions could not be refreshed: " + exception.Message : exception.Message;
        }
    }

    public void Dispose()
    {
        _closed.Cancel();
        foreach (var host in Hosts)
        {
            host.Dispose();
        }
        _closed.Dispose();
    }
}

/// <summary>A host log: replay first, then deliver each numbered live event without coalescing.</summary>
public sealed partial class AgentPanel : ObservableObject, IDisposable
{
    private readonly UiServices _services;
    private readonly string _project;
    private readonly CancellationTokenSource _closed = new();
    private readonly CancellationToken _token;
    private readonly SortedDictionary<long, AgentEvent> _pending = [];
    private readonly DispatcherTimer _timer;
    private long _last;
    private bool _polling;
    private bool _replayed;

    private readonly string _task;
    private readonly string _client;
    private readonly Func<Task> _refresh;
    private string _sessionId;

    public AgentPanel(UiServices services, string project, string task, AgentHost host, Func<Task> refresh)
    {
        _services = services;
        _token = _closed.Token;
        _project = project;
        _task = task;
        _client = host.Client;
        _sessionId = host.SessionId;
        _refresh = refresh;
        Key = host.Key;
        Heading = $"{Agents.Client(host.Client)} · host {host.Key[..Math.Min(8, host.Key.Length)]}"
            + (host.PermissionMode.Length > 0 ? $" · {host.PermissionMode} mode" : "")
            + (host.ModelUnlisted ? $" · asked for {host.RequestedModel} (not in the model list)" : "");
        Status = host.Status;
        Alive = host.Alive;
        services.Live.AgentReceived += Receive;
        services.Live.PropertyChanged += LiveChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += Tick;
        if (Alive)
        {
            _timer.Start();
        }
        _ = PollAsync();
    }

    public string Key { get; }
    public string Heading { get; }
    public ObservableCollection<AgentLogRow> Entries { get; } = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    public partial string Status { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    public partial bool Alive { get; private set; }
    [ObservableProperty]
    public partial string Message { get; set; } = "";
    [ObservableProperty]
    public partial string? Error { get; private set; }
    [ObservableProperty]
    public partial string ResumePrompt { get; set; } = "Go on.";
    public bool CanSend => Alive && Status is not "running";
    public bool CanStop => Alive && Status is "running";
    public bool CanResume => !Alive && _sessionId.Length > 0;

    private void Receive(object? sender, AgentBatchEventArgs batch)
    {
        if (batch.Project != _project || batch.Key != Key || _closed.IsCancellationRequested)
        {
            return;
        }
        foreach (var item in batch.Events.Where(e => e.Seq > _last))
        {
            _pending[item.Seq] = item;
        }
        if (_replayed && !_polling)
        {
            Drain();
            if (_pending.Count > 0)
            {
                // A stream batch raced the log read. Retry on the next timer tick even when the stream is live.
                _replayed = false;
            }
            if (_pending.Count > 0)
            {
                _ = PollAsync();
            }
        }
    }

    private void LiveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveUpdates.State) && _services.Live.State is "live" && Alive)
        {
            _ = PollAsync();
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_services.Live.State is not "live" || !_replayed)
        {
            _ = PollAsync();
        }
    }

    public async Task PollAsync()
    {
        if (_polling || _closed.IsCancellationRequested)
        {
            return;
        }
        _polling = true;
        try
        {
            var log = await _services.Api.AgentLogAsync(_project, Key, _last, _token);
            Status = log.Status;
            foreach (var item in log.Events.Where(e => e.Seq > _last))
            {
                _pending[item.Seq] = item;
            }
            _replayed = true;
            Drain();
            if (_pending.Count > 0)
            {
                _replayed = false;
            }
            if (!log.Alive)
            {
                Alive = false;
                Status = log.Status;
                _timer.Stop();
            }
            Error = null;
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { /* Dialog closed. */ }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    private void Drain()
    {
        while (_pending.Remove(_last + 1, out var item))
        {
            _last = item.Seq;
            switch (item.Event)
            {
                case "status":
                    Status = item.Status ?? Status;
                    if (Status is "closed" or "failed")
                    {
                        Alive = false;
                        _timer.Stop();
                    }
                    break;
                case "answered":
                    if (Entries.FirstOrDefault(e => e.Id == item.Id) is { } approval)
                    {
                        approval.Answered(item.Allow);
                    }
                    break;
                case "user" or "text" or "tool" or "error" or "turn" or "session" or "approval":
                    if (item.Event is "session" && item.SessionId is { Length: > 0 } sessionId)
                    {
                        _sessionId = sessionId;
                        OnPropertyChanged(nameof(CanResume));
                    }
                    Entries.Add(new AgentLogRow(this, item));
                    break;
                default:
                    break;
            }
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Message.Trim().Length > 0 && await CommandAsync("send", new AgentCommand { Text = Message }))
        {
            Message = "";
        }
    }

    [RelayCommand]
    private Task<bool> StopAsync() => CommandAsync("stop", new AgentCommand());
    [RelayCommand]
    private Task<bool> CloseAsync() => CommandAsync("close", new AgentCommand());

    [RelayCommand]
    private async Task ResumeAsync()
    {
        try
        {
            await _services.Api.StartAgentAsync(_project, _task, new AgentStart
            {
                Client = _client, Prompt = ResumePrompt, Resume = _sessionId,
            }, _token);
            await _refresh();
            Error = null;
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { /* Dialog closed. */ }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
        }
    }

    internal async Task<bool> CommandAsync(string action, AgentCommand body)
    {
        try
        {
            await _services.Api.AgentCommandAsync(_project, Key, action, body, _token);
            Error = null;
            return true;
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Error = exception.Message;
            return false;
        }
    }

    public void Dispose()
    {
        _services.Live.AgentReceived -= Receive;
        _services.Live.PropertyChanged -= LiveChanged;
        _timer.Stop();
        _timer.Tick -= Tick;
        _closed.Cancel();
        _closed.Dispose();
    }
}

public sealed partial class AgentLogRow : ObservableObject
{
    private readonly AgentPanel _panel;
    public AgentLogRow(AgentPanel panel, AgentEvent item)
    {
        _panel = panel;
        Id = item.Id;
        IsApproval = item.Event is "approval";
        Text = item.Event switch
        {
            "user" => "You: " + item.Text,
            "tool" => (item.Tool ?? "tool") + ": " + item.Text,
            "approval" => "Wants to use " + item.Tool + ": " + item.Text,
            "turn" => TurnText(item),
            "session" => "Session " + item.SessionId + " · " + item.Model,
            _ => item.Text ?? "",
        };
    }

    public string? Id { get; }
    public string Text { get; }
    private static string TurnText(AgentEvent item)
    {
        var label = item.Status switch
        {
            "completed" => "Turn done",
            "stopped" => "Turn stopped",
            "failed" => "Turn failed",
            null => "Turn",
            _ => item.Status,
        };
        return label + (item.DurationMs > 0 ? " · " + Agents.Duration(item.DurationMs) : "")
            + (item.CostUsd is { } cost ? " · $" + cost.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " API-equivalent (session so far)" : "")
            + (item.Error is { Length: > 0 } error ? ": " + error : "");
    }
    [ObservableProperty]
    public partial bool IsApproval { get; private set; }
    [ObservableProperty]
    public partial string? Answer { get; private set; }
    public void Answered(bool allow)
    {
        IsApproval = false;
        Answer = allow ? "allowed" : "denied";
    }
    [RelayCommand]
    private Task<bool> AllowAsync() => _panel.CommandAsync("approve", new AgentCommand { Id = Id, Allow = true });
    [RelayCommand]
    private Task<bool> DenyAsync() => _panel.CommandAsync("approve", new AgentCommand { Id = Id, Allow = false });
}
