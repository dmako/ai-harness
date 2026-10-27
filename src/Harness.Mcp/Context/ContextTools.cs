using System.Text.Json;
using Harness.Core.Providers;
using Harness.Core.Retrieval;
using Harness.Core.Serialization;
using Harness.Mcp.Workspaces;

namespace Harness.Mcp.Context;

public sealed class ContextTools(
    IProviderRegistry? providers = null,
    IContextProjectSession? project = null,
    WorkspaceTools? workspace = null
)
{
    public async Task<ContextAnswer> CallAsync(IDictionary<string, JsonElement>? arguments, CancellationToken token)
    {
        var (task, path, scope) = Validated(arguments);
        var request = new ContextRequest(
            task,
            Integer(arguments, "budget"),
            Integer(arguments, "tokens"),
            arguments?.TryGetValue("debug", out var debug) is true && debug.GetBoolean()
        );
        var bytes = ContextRenderer.BudgetBytes(request);
        if (ContextJson.Size(new JsonObjectMap { ["task"] = task }) + 200 > bytes)
        {
            throw new ArgumentException(
                "task metadata exceeds the context budget; raise the budget or shorten the task"
            );
        }

        var context = project is null
            ? new ContextProject(new(), Notes: ["no project memory (run harness init to record decisions)"])
            : await project.ReadContextProjectAsync(token).ConfigureAwait(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        var gathered = await RetrievalGatherer
            .GatherAsync(context.Sources with { Registry = providers }, new(task, path), linked.Token)
            .ConfigureAwait(false);
        context = await AnnotatedAsync(context, linked.Token).ConfigureAwait(false);
        var section = scope is "workspace"
            ? await workspace!
                .ContextAsync(
                    task,
                    gathered.Symbols,
                    gathered.Phrases,
                    String(arguments, "depth") is "deep",
                    bytes / 4,
                    linked.Token
                )
                .ConfigureAwait(false)
            : null;
        return await ContextRenderer
            .RenderAsync(request, gathered, context, section, linked.Token)
            .ConfigureAwait(false);
    }

    /// <summary>The task, path and scope, checked in that order with depth and the workspace.</summary>
    private (string Task, string Path, string Scope) Validated(IDictionary<string, JsonElement>? arguments)
    {
        var task = String(arguments, "task").Trim();
        if (task.Length is 0)
        {
            throw new ArgumentException("task must not be empty: describe what you are about to do");
        }

        var path = String(arguments, "path");
        if (path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(path))
        {
            throw new ArgumentException("path must be relative to the project root");
        }

        var scope = String(arguments, "scope");
        if (scope is not ("" or "project" or "workspace"))
        {
            throw new ArgumentException("scope must be project or workspace");
        }

        if (String(arguments, "depth") is not ("" or "light" or "deep"))
        {
            throw new ArgumentException("depth must be light or deep");
        }

        if (scope is "workspace" && workspace is null)
        {
            throw new InvalidOperationException(
                "this project is not in a workspace; use 'harness workspace create' to add it"
            );
        }

        return (task, path, scope);
    }

    /// <summary>The context with the providers' freshness and, when readable, the findings.</summary>
    private async Task<ContextProject> AnnotatedAsync(ContextProject context, CancellationToken token)
    {
        if (project is IProviderProjectSession freshness)
        {
            var current = await freshness.ReadProviderProjectAsync(token).ConfigureAwait(false);
            context = context with { Stale = current.Stale, CrossBranch = current.CrossBranch };
        }
        if (context.ReadFindings is { } readFindings)
        {
            try
            {
                context = context with { Findings = await readFindings(token).ConfigureAwait(false) };
            }
            catch (Exception error)
                when (error
                        is IOException
                            or InvalidDataException
                            or InvalidOperationException
                            or UnauthorizedAccessException
                )
            { /* Findings are optional context annotations. */
            }
        }
        return context;
    }

    private static string String(IDictionary<string, JsonElement>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) is true ? value.GetString() ?? "" : "";

    private static int Integer(IDictionary<string, JsonElement>? arguments, string key) =>
        arguments?.TryGetValue(key, out var value) is true ? value.GetInt32() : 0;
}
