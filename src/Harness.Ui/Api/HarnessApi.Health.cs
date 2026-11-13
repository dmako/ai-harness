using System.Text.Json;

namespace Harness.Ui.Api;

/// <summary>The health area: doctor's checks and the fixes harness runs.</summary>
public sealed partial class HarnessApi
{
    /// <summary>
    /// The project's health; with <paramref name="probe"/> the providers are started to check them, which harness ui
    /// allows only from its own page (X-Harness-UI).
    /// </summary>
    public async Task<HealthReport> HealthAsync(string projectId, bool probe, CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/projects/{Uri.EscapeDataString(projectId)}/health" + (probe ? "?probe=1" : "")
        );
        if (probe)
        {
            request.Headers.Add("X-Harness-UI", "1");
        }
        using var response = await http.SendAsync(request, token);
        await EnsureAsync(response, token);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(token), UiJsonContext.Default.HealthReport)
            ?? throw new HarnessApiException("health: empty answer");
    }

    /// <summary>Runs (or with Preview, shows) a fix; a fix that runs but fails comes back with Ok false.</summary>
    public async Task<FixResult> FixAsync(string projectId, FixRequest fix, CancellationToken token)
    {
        var answer = await PostAsync(
            $"api/projects/{Uri.EscapeDataString(projectId)}/fix",
            fix,
            UiJsonContext.Default.FixRequest,
            token
        );
        return answer.Deserialize(UiJsonContext.Default.FixResult) ?? throw new HarnessApiException("fix: empty answer");
    }
}
