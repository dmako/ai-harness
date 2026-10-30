using System.Text.Json.Nodes;
using Harness.Core.Memory;
using Harness.Core.Web;
using Harness.Infrastructure.Memory;
using Harness.Infrastructure.Telemetry;

namespace Harness.Cli.Ui;

internal sealed partial class UiReadService
{
    /// <summary>
    /// The author's own knowledge for the project, at a project, path, subsystem or branch scope (a wider level is
    /// added on its own page); something similar already recorded is answered instead (needs_decision).
    /// </summary>
    private static JsonObject AddMemory(MemoryStore store, JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "type", "content", "scope", "source_ref");
        var text = fields.Text("scope").Trim();
        var scope = MemoryScope.Parse(text.Length is 0 ? "project" : text);
        if (scope.Wider)
        {
            throw new UiRequestException(
                400,
                $"scope {text}: project, path:, subsystem: or branch: (a wider level is added on its own page)"
            );
        }

        var reference = fields.Text("source_ref").Trim();
        var result = store.Put(
            new()
            {
                Type = MemoryText.ParseType(fields.Text("type")),
                Content = fields.Text("content"),
                Scope = scope,
                Source = new("user", reference.Length is 0 ? "harness ui" : reference, HumanActor.Current()),
            },
            token
        );
        JsonObject view = new()
        {
            ["status"] = result.Status,
            ["similar"] = result.Similar.Count is 0
                ? null
                : UiJson.Node(result.Similar.Select(s => MemoryViews.Memory(s.Memory)).ToArray()),
        };
        if (result.Memory is not null)
        {
            view["memory"] = UiJson.Node(MemoryViews.Memory(result.Memory));
        }
        return view;
    }

    /// <summary>
    /// Developer telemetry on or off, as 'harness telemetry enable/disable': on needs the collector's endpoint (the
    /// last one is kept when none is given) and keeps the stored token when none is given for the same endpoint. The
    /// answer is the machine view, which never carries the token.
    /// </summary>
    private async Task<JsonNode?> SetTelemetryAsync(JsonObject body, CancellationToken token)
    {
        var fields = new UiWriteBody(body, "enabled", "endpoint", "token");
        var export = new DeveloperExport(GlobalDirectory);
        var state = export.Load();
        if (fields.Flag("enabled"))
        {
            var endpoint = fields.Text("endpoint").Trim();
            endpoint = endpoint.Length is 0 ? state.Endpoint : endpoint;
            if (endpoint.Length is 0)
            {
                throw new UiRequestException(400, "the collector's endpoint is required");
            }
            var secret = fields.Text("token");
            DeveloperExport.Enable(
                state,
                endpoint,
                secret.Length is 0 && endpoint == state.Endpoint ? state.Token : secret
            );
        }
        else
        {
            state.Enabled = false;
        }
        await export.SaveAsync(state, token);
        return Machine();
    }

    /// <summary>
    /// Health's Fix it: the named check's fix as harness computes it now, run only when it is one of the harness
    /// commands a fix may run; {"sync": true} syncs a stale index. Answers {ok, message, output}.
    /// </summary>
    private async Task<JsonNode?> FixAsync(JsonObject project, JsonObject body, CancellationToken token)
    {
        RequirePresent(project);
        var fields = new UiWriteBody(body, "check", "sync");
        var root = UiProjectCatalog.Text(project, "root");
        if (fields.Flag("sync"))
        {
            return await HarnessFix.RunAsync(root, ["sync"], token);
        }

        var name = fields.Text("check");
        var (_, checks) = await ChecksAsync(project, token);
        return checks.FirstOrDefault(c => c.Name == name && HarnessFix.Fixable(c)) is { Fix: { } fix }
            ? await HarnessFix.RunAsync(root, HarnessFix.Command(fix)!, token)
            : throw new UiRequestException(400, $"no fix to run for {name}: it passes now, or its fix is yours to do");
    }
}
