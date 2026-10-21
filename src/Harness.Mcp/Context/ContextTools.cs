using Harness.Mcp.Workspaces;
using System.Text.Json;
using Harness.Core.Providers;
using Harness.Core.Retrieval;

namespace Harness.Mcp.Context;

public sealed class ContextTools(IProviderRegistry? providers = null, IContextProjectSession? project = null, WorkspaceTools? workspace = null)
{
    public async Task<ContextAnswer> CallAsync(IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var task = String("task").Trim();
        if (task.Length is 0)
            throw new ArgumentException("task must not be empty: describe what you are about to do");
        var path = String("path");
        if (path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(path))
            throw new ArgumentException("path must be relative to the project root");
        var scope = String("scope");
        if (scope is not ("" or "project" or "workspace"))
            throw new ArgumentException("scope must be project or workspace");
        if (String("depth") is not ("" or "light" or "deep"))
            throw new ArgumentException("depth must be light or deep");
        if (scope is "workspace" && workspace is null)
            throw new InvalidOperationException("this project is not in a workspace; use 'harness workspace create' to add it");
        var request = new ContextRequest(task, Integer("budget"), Integer("tokens"), arguments?.TryGetValue("debug", out var debug) is true && debug.GetBoolean());
        var bytes = ContextRenderer.BudgetBytes(request);
        if (ContextJson.Size(new { task }) + 200 > bytes)
            throw new ArgumentException("task metadata exceeds the context budget; raise the budget or shorten the task");
        var context = project is null ? new ContextProject(new(), Notes: ["no project memory (run harness init to record decisions)"]) : await project.ReadContextProjectAsync(token).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var gathered = await RetrievalGatherer.GatherAsync(context.Sources with { Registry = providers }, new(task, path), linked.Token).ConfigureAwait(false);
        if (project is IProviderProjectSession freshness)
        {
            var current = await freshness.ReadProviderProjectAsync(linked.Token).ConfigureAwait(false);
            context = context with { Stale = current.Stale, CrossBranch = current.CrossBranch };
        }
        var section = scope is "workspace" ? await workspace!.ContextAsync(task, gathered.Symbols, gathered.Phrases, String("depth") is "deep", bytes / 4, linked.Token).ConfigureAwait(false) : null;
        return await ContextRenderer.RenderAsync(request, gathered, context, section, linked.Token).ConfigureAwait(false);

        string String(string key) => arguments?.TryGetValue(key, out var value) is true ? value.GetString() ?? "" : "";
        int Integer(string key) => arguments?.TryGetValue(key, out var value) is true ? value.GetInt32() : 0;
    }
}
