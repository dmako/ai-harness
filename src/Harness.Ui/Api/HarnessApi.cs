using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Harness.Ui.Api;

/// <summary>
/// The harness ui HTTP API, typed. <paramref name="http"/> has the harness ui address as its BaseAddress. Every write
/// goes through <see cref="PostAsync{TBody}"/>: harness ui takes a write only with X-Harness-UI: 1 and an Origin of
/// its own address; a browser sends Origin itself, another host (desktop, tests) sets <see cref="Origin"/>.
/// </summary>
public sealed class HarnessApi(HttpClient http)
{
    /// <summary>Lets a host stream a response body as it arrives (the browser's fetch buffers it otherwise).</summary>
    public Action<HttpRequestMessage>? Streaming { get; init; }

    /// <summary>The Origin to send with writes (http://host:port), for hosts that are not a page of harness ui.</summary>
    public string? Origin { get; init; }

    public async Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken token) =>
        (await GetAsync("api/projects", UiJsonContext.Default.ProjectList, token)).Projects;

    public Task<BoardResponse> BoardAsync(string projectId, CancellationToken token) =>
        GetAsync($"api/projects/{Uri.EscapeDataString(projectId)}/board", UiJsonContext.Default.BoardResponse, token);

    /// <summary>Saves this machine's Board choices for the project (kept in ui.json, shared by every browser).</summary>
    public Task SetBoardAsync(string projectId, BoardChange change, CancellationToken token) =>
        PostAsync($"api/projects/{Uri.EscapeDataString(projectId)}/board", change, UiJsonContext.Default.BoardChange, token);

    /// <summary>A read as JSON, for answers that have no typed contract yet; fails with the server's message.</summary>
    public async Task<JsonElement> ReadAsync(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, token);
        await EnsureAsync(response, token);
        return JsonElement.Parse(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary>
    /// A write: POSTs <paramref name="body"/> as JSON with the headers harness ui requires and fails with the server's
    /// message; the answer is returned for writes that send something back.
    /// </summary>
    public async Task<JsonElement> PostAsync<TBody>(
        string path,
        TBody body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TBody> type,
        CancellationToken token
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, type) };
        request.Headers.Add("X-Harness-UI", "1");
        if (Origin is { Length: > 0 } origin)
        {
            request.Headers.Add("Origin", origin);
        }
        using var response = await http.SendAsync(request, token);
        await EnsureAsync(response, token);
        var text = await response.Content.ReadAsStringAsync(token);
        return text.Length is 0 ? default : JsonElement.Parse(text);
    }

    /// <summary>
    /// /api/events as it comes: each "change" event, until <paramref name="token"/> ends it or the stream drops
    /// (the caller reconnects). Comments, heartbeats and "agent" events are skipped; <paramref name="connected"/>
    /// is called once the stream is open.
    /// </summary>
    public async IAsyncEnumerable<LiveChange> ChangesAsync(
        Action? connected,
        [EnumeratorCancellation] CancellationToken token
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/events");
        Streaming?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await EnsureAsync(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        connected?.Invoke();
        await foreach (var (name, data) in ServerSentEvents.ReadAsync(stream, token))
        {
            if (name is "change" && JsonSerializer.Deserialize(data, UiJsonContext.Default.LiveChange) is { } change)
            {
                yield return change;
            }
        }
    }

    private async Task<T> GetAsync<T>(
        string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken token
    )
    {
        using var response = await http.GetAsync(path, token);
        await EnsureAsync(response, token);
        return await response.Content.ReadFromJsonAsync(type, token)
            ?? throw new HarnessApiException($"{path}: empty answer");
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        string message;
        try
        {
            message =
                (await response.Content.ReadFromJsonAsync(UiJsonContext.Default.ApiError, token))?.Error
                ?? response.ReasonPhrase
                ?? "";
        }
        catch (JsonException)
        {
            message = response.ReasonPhrase ?? "";
        }
        throw new HarnessApiException($"{(int)response.StatusCode}: {message}");
    }
}

/// <summary>A harness ui request that failed, with the server's message.</summary>
public sealed class HarnessApiException(string message) : Exception(message)
{
    public HarnessApiException()
        : this("harness ui request failed") { }

    public HarnessApiException(string message, Exception inner)
        : this(message + ": " + inner.Message) { }
}
