using System.Text.Json;

namespace Harness.Ui.Api;

/// <summary>The machine area: the installation, Board defaults, telemetry, SCM connections, graphify, search and docs.</summary>
public sealed partial class HarnessApi
{
    public Task<MachineInfo> MachineAsync(CancellationToken token) => GetAsync("api/machine", UiJsonContext.Default.MachineInfo, token);

    public Task<BoardDefaultsAnswer> BoardDefaultsAsync(CancellationToken token) =>
        GetAsync("api/board-defaults", UiJsonContext.Default.BoardDefaultsAnswer, token);

    public async Task<BoardDefaults> SetBoardDefaultsAsync(BoardDefaultsChange change, CancellationToken token) =>
        Read(await PostAsync("api/board-defaults", change, UiJsonContext.Default.BoardDefaultsChange, token), UiJsonContext.Default.BoardDefaultsAnswer)
            .Defaults;

    public async Task<TelemetryState> SetTelemetryAsync(TelemetryChange change, CancellationToken token) =>
        Read(await PostAsync("api/telemetry", change, UiJsonContext.Default.TelemetryChange, token), UiJsonContext.Default.TelemetryAnswer).Telemetry;

    public Task<ScmList> ScmAsync(CancellationToken token) => GetAsync("api/scm", UiJsonContext.Default.ScmList, token);

    /// <summary>Adds an SCM connection, or edits <paramref name="editing"/> (an empty token keeps the stored one).</summary>
    public Task SaveScmAsync(string? editing, ScmForm form, CancellationToken token) =>
        PostAsync(
            editing is null ? "api/scm" : $"api/scm/{Uri.EscapeDataString(editing)}/edit",
            form,
            UiJsonContext.Default.ScmForm,
            token
        );

    public Task RemoveScmAsync(string name, CancellationToken token) =>
        PostAsync($"api/scm/{Uri.EscapeDataString(name)}/remove", Nothing, UiJsonContext.Default.DictionaryStringString, token);

    /// <summary>Tests a saved connection by name, or the form's values as typed (aliases are not tested).</summary>
    public async Task<ScmTest> TestScmAsync(ScmForm form, CancellationToken token) =>
        Read(
            await PostAsync("api/scm/test", new ScmForm { Name = form.Name, Type = form.Type, Address = form.Address, Token = form.Token }, UiJsonContext.Default.ScmForm, token),
            UiJsonContext.Default.ScmTestAnswer
        ).Test;

    public Task<GraphifySessions> GraphifySessionsAsync(string? projectId, CancellationToken token) =>
        GetAsync(
            "api/graphify/sessions" + (projectId is null ? "" : "?project=" + Uri.EscapeDataString(projectId)),
            UiJsonContext.Default.GraphifySessions,
            token
        );

    /// <summary>start or restart a project's session, or stop a session.</summary>
    public Task GraphifyAsync(string action, GraphifyAction body, CancellationToken token) =>
        PostAsync($"api/graphify/sessions/{action}", body, UiJsonContext.Default.GraphifyAction, token);

    public Task<SearchAnswer> SearchAsync(string query, CancellationToken token) =>
        GetAsync("api/search?q=" + Uri.EscapeDataString(query), UiJsonContext.Default.SearchAnswer, token);

    public Task<DocText> DocAsync(string slug, CancellationToken token) =>
        GetAsync("api/docs/" + Uri.EscapeDataString(slug), UiJsonContext.Default.DocText, token);

    private static T Read<T>(JsonElement answer, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        answer.Deserialize(type) ?? throw new HarnessApiException("empty answer");
}
