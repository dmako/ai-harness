using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Harness.Ui.Api;

/// <summary>
/// The harness ui HTTP API, typed. <paramref name="http"/> has the harness ui address as its BaseAddress. A browser
/// sends Origin itself; another host (desktop, tests) sets the Origin header harness ui requires on writes.
/// </summary>
public sealed class HarnessApi(HttpClient http)
{
    /// <summary>Lets a host stream a response body as it arrives (the browser's fetch buffers it otherwise).</summary>
    public Action<HttpRequestMessage>? Streaming { get; init; }

    public async Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken token) =>
        (await GetAsync("api/projects", UiJsonContext.Default.ProjectList, token)).Projects;

    public Task<BoardResponse> BoardAsync(string projectId, CancellationToken token) =>
        GetAsync($"api/projects/{Uri.EscapeDataString(projectId)}/board", UiJsonContext.Default.BoardResponse, token);

    /// <summary>Saves this machine's Board choices for the project (kept in ui.json, shared by every browser).</summary>
    public async Task SetBoardAsync(string projectId, BoardChange change, CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/projects/{Uri.EscapeDataString(projectId)}/board"
        )
        {
            Content = JsonContent.Create(change, UiJsonContext.Default.BoardChange),
        };
        request.Headers.Add("X-Harness-UI", "1");
        using var response = await http.SendAsync(request, token);
        await EnsureAsync(response, token);
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
