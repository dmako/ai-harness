using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Harness.Ui.Api;

/// <summary>The live area: /api/events.</summary>
public sealed partial class HarnessApi
{
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

}
