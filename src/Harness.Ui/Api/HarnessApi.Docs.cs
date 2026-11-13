namespace Harness.Ui.Api;

/// <summary>The docs area: the guide and reference harness ui serves.</summary>
public sealed partial class HarnessApi
{
    /// <summary>The documents and their sections (GET /api/docs).</summary>
    public Task<DocList> DocsAsync(CancellationToken token) => GetAsync("api/docs", UiJsonContext.Default.DocList, token);
}
