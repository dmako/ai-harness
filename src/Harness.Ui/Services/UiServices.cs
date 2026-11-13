using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Harness.Ui.Api;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Services;

/// <summary>
/// What every view model of the page shares: the API, the host, toasts, dialogs and live updates. Live updates wait
/// while a dialog is open.
/// </summary>
public sealed class UiServices : IDisposable
{
    public UiServices(HarnessApi api, UiHostContext host, LiveUpdates? live = null, ToastService? toasts = null)
    {
        Api = api;
        Host = host;
        Dialogs = new DialogService();
        Toasts = toasts ?? new ToastService();
        Live = live ?? new LiveUpdates(() => Dialogs.IsOpen);
    }

    public HarnessApi Api { get; }
    public UiHostContext Host { get; }
    public DialogService Dialogs { get; }
    public ToastService Toasts { get; }
    public LiveUpdates Live { get; }

    /// <summary>
    /// A write from the page (app.js post() then afterWrite()): sent through the API's one write path, then "Done"
    /// and every section reloaded; a refusal is a toast with the server's message and is thrown on.
    /// </summary>
    public async Task<JsonElement> WriteAsync<TBody>(
        string path,
        TBody body,
        JsonTypeInfo<TBody> type,
        CancellationToken token,
        string done = "Done"
    )
    {
        try
        {
            var answer = await Api.PostAsync(path, body, type, token);
            Toasts.Show(done);
            Live.RefreshAll();
            return answer;
        }
        catch (Exception exception) when (exception is HarnessApiException or HttpRequestException)
        {
            Toasts.Fail("Not done", exception.Message);
            throw;
        }
    }

    public void Dispose() => Live.Dispose();
}
