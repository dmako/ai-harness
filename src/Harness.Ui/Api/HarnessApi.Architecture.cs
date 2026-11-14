namespace Harness.Ui.Api;

/// <summary>The architecture area: structure, stack, complexity, change coupling, package marks and findings' triage.</summary>
public sealed partial class HarnessApi
{
    public Task<StructureAnswer> StructureAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "structure"), UiJsonContext.Default.StructureAnswer, token);

    public Task<StackAnswer> StackAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "stack"), UiJsonContext.Default.StackAnswer, token);

    public Task<ComplexityAnswer> ComplexityAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "complexity"), UiJsonContext.Default.ComplexityAnswer, token);

    /// <summary>Change coupling, asked from harness ui's own page: the partners may start providers.</summary>
    public async Task<CouplingAnswer> CouplingAsync(string projectId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Project(projectId, "coupling"));
        request.Headers.Add("X-Harness-UI", "1");
        using var response = await http.SendAsync(request, token);
        await EnsureAsync(response, token);
        return System.Text.Json.JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(token), UiJsonContext.Default.CouplingAnswer)
            ?? throw new HarnessApiException("coupling: empty answer");
    }

    /// <summary>Newer stable versions and known vulnerabilities of these NuGet packages (name@version each).</summary>
    public Task<PackageMarks> PackageMarksAsync(string projectId, IEnumerable<string> packages, CancellationToken token) =>
        GetAsync(
            Project(projectId, "packages?packages=" + Uri.EscapeDataString(string.Join(',', packages))),
            UiJsonContext.Default.PackageMarks,
            token
        );

    /// <summary>A finding's triage: mark (by design, with a reason), task (in a member), or unmark.</summary>
    public Task FindingAsync(string projectId, string action, FindingAction body, CancellationToken token) =>
        PostAsync(Project(projectId, "findings/" + action), body, UiJsonContext.Default.FindingAction, token);
}
