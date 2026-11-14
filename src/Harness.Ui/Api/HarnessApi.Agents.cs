using System.Globalization;

namespace Harness.Ui.Api;

/// <summary>The agents area: providers, the project's SCM and hooks.</summary>
public sealed partial class HarnessApi
{
    public Task<ProvidersAnswer> ProvidersAsync(string projectId, int days, CancellationToken token) =>
        GetAsync(Project(projectId, "providers?days=" + days.ToString(CultureInfo.InvariantCulture)), UiJsonContext.Default.ProvidersAnswer, token);

    public Task<ProjectScm> ProjectScmAsync(string projectId, int days, CancellationToken token) =>
        GetAsync(Project(projectId, "scm?days=" + days.ToString(CultureInfo.InvariantCulture)), UiJsonContext.Default.ProjectScm, token);

    /// <summary>Chooses the connection this project uses on this machine ("" automatic); the card without its use.</summary>
    public async Task<ProjectScm> ChooseScmAsync(string projectId, string name, CancellationToken token) =>
        Read(await PostAsync(Project(projectId, "scm/choice"), new ScmChoice { Name = name }, UiJsonContext.Default.ScmChoice, token), UiJsonContext.Default.ProjectScm);

    public async Task<ProjectScmTest> TestProjectScmAsync(string projectId, CancellationToken token) =>
        Read(
            await PostAsync(Project(projectId, "scm/test"), Nothing, UiJsonContext.Default.DictionaryStringString, token),
            UiJsonContext.Default.ProjectScmTestAnswer
        ).Test;

    /// <summary>
    /// The project's architecture as the providers give it (GET …/architecture, from harness ui's own page: it starts
    /// them); read as JSON until the Architecture view types it.
    /// </summary>
    public async Task<System.Text.Json.JsonElement> ArchitectureRawAsync(string projectId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Project(projectId, "architecture"));
        request.Headers.Add("X-Harness-UI", "1");
        using var response = await http.SendAsync(request, token);
        await EnsureAsync(response, token);
        return System.Text.Json.JsonElement.Parse(await response.Content.ReadAsStringAsync(token));
    }

    public Task<HooksAnswer> HooksAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "hooks"), UiJsonContext.Default.HooksAnswer, token);
}
