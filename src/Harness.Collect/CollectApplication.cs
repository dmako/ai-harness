using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Infrastructure.Telemetry;
using Harness.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Harness.Collect;

public static partial class CollectApplication
{
    public const int MaxBody = 1 << 20;

    [GeneratedRegex("^m_[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex MachineId { get; }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The application stopping registration owns and disposes the semaphore."
    )]
    public static WebApplication Create(string directory, string address = "", bool lan = false, string token = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        var app = CompanionHost.Create(address, lan, 8766);
        var gate = new SemaphoreSlim(1);
        app.Lifetime.ApplicationStopped.Register(gate.Dispose);
        app.MapPost(
            "/v1/report",
            async (HttpContext context) =>
            {
                if (
                    token.Length > 0
                    && !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()),
                        Encoding.UTF8.GetBytes("Bearer " + token)
                    )
                )
                {
                    return Results.Text("unauthorized\n", statusCode: 401);
                }
                using var body = new MemoryStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var buffer = new byte[8192];
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    if (body.Length + read > MaxBody)
                    {
                        return Results.Text("report too large\n", statusCode: 413);
                    }
                    body.Write(buffer, 0, read);
                }
                JsonNode? report;
                try
                {
                    report = JsonNode.Parse(new UTF8Encoding(false, true).GetString(body.ToArray()));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or DecoderFallbackException)
                {
                    return Results.Text("not a harness report (schema 1 with a machine id)\n", statusCode: 400);
                }
                if (
                    report is not JsonObject obj
                    || obj["schema"] is not JsonValue schema
                    || !schema.TryGetValue<int>(out var version)
                    || version is not 1
                    || obj["machine"] is not JsonValue machine
                    || !machine.TryGetValue<string>(out var id)
                    || !MachineId.IsMatch(id)
                )
                {
                    return Results.Text("not a harness report (schema 1 with a machine id)\n", statusCode: 400);
                }
                await gate.WaitAsync(timeout.Token);
                try
                {
                    var dir = Path.Combine(directory, id);
                    if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) is not 0)
                    {
                        return Results.StatusCode(400);
                    }
                    Directory.CreateDirectory(dir);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(
                            dir,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        );
                    }
                    var bytes = body.ToArray();
                    var file =
                        DateTimeOffset.UtcNow.ToString(
                            "yyyyMMdd'T'HHmmss.fff'Z'",
                            System.Globalization.CultureInfo.InvariantCulture
                        ) + ".json";
                    PrivateExportFiles.Write(Path.Combine(dir, file), bytes);
                    var temp = Path.Combine(dir, ".latest.tmp");
                    PrivateExportFiles.Write(temp, bytes);
                    File.Move(temp, Path.Combine(dir, "latest.json"), true);
                }
                finally
                {
                    gate.Release();
                }
                return Results.StatusCode(202);
            }
        );
        app.MapMethods(
            "/machines/{id}",
            ["GET", "HEAD"],
            (string id) =>
            {
                id = id.EndsWith(".json", StringComparison.Ordinal) ? id[..^5] : id;
                var file = Path.Combine(directory, id, "latest.json");
                return
                    MachineId.IsMatch(id) && File.Exists(file) && !IsLink(file) && !IsLink(Path.GetDirectoryName(file)!)
                    ? Results.Bytes(File.ReadAllBytes(file), "application/json")
                    : Results.NotFound();
            }
        );
        app.MapMethods(
            "/",
            ["GET", "HEAD"],
            (HttpContext context) => CompanionHost.Html(context, "harness telemetry", MachinesPage(directory))
        );
        return app;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) is not 0;

    private static string MachinesPage(string directory)
    {
        var html = new StringBuilder(
            "<h1>harness telemetry</h1><p>Opt-in aggregated reports from harness machines. No code, paths, queries or project names.</p><table><tr><th>Machine</th><th>Last report</th><th>harness</th><th>Projects</th><th>Answers</th><th>Context hits</th><th>Findings</th><th>Reports</th></tr>"
        );
        List<JsonNode> latest = [];
        foreach (
            var dir in Directory
                .EnumerateDirectories(directory)
                .Where(d => MachineId.IsMatch(Path.GetFileName(d)) && !IsLink(d))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "latest.json")))
        )
        {
            try
            {
                var file = Path.Combine(dir, "latest.json");
                if (IsLink(file))
                {
                    continue;
                }
                var report = JsonNode.Parse(File.ReadAllBytes(file))!;
                latest.Add(report);
                var projects = report["projects"]?.AsArray() ?? [];
                var hits = projects.Sum(p => p?["context"]?["code_hits"]?.GetValue<int>() ?? 0);
                var changed = projects.Sum(p => p?["context"]?["code_changed"]?.GetValue<int>() ?? 0);
                var id = WebUtility.HtmlEncode(Path.GetFileName(dir));
                html.Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"<tr><td><a href=\"/machines/{id}.json\">{id}</a></td><td>{File.GetLastWriteTimeUtc(file):u}</td><td>{WebUtility.HtmlEncode(report["harness"]?["version"]?.ToString())}<br>{WebUtility.HtmlEncode(report["harness"]?["platform"]?.ToString())}</td><td>{projects.Count}</td><td>{projects.Sum(p => p?["context"]?["answers"]?.GetValue<int>() ?? 0)}</td><td>{(changed > 0 ? (double)hits / changed : 0):P0}</td><td>{projects.Sum(p => p?["findings"]?.AsArray().Count ?? 0)}</td><td>{Directory.GetFiles(dir, "2*.json").Length}</td></tr>"
                );
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
            { /* Ignore incomplete reports. */
            }
        }
        return html.Append(
                "</table><p>On a machine: harness telemetry enable --endpoint http://&lt;this host&gt;:8766/v1/report</p>"
            )
            .Append(DeveloperView(latest))
            .ToString();
    }

    /// <summary>
    /// The harness developer's view over every machine's latest report: get_context relevance per provider,
    /// capability and language, the tools' latency and errors, and the findings by audience (in how many projects).
    /// </summary>
    internal static string DeveloperView(IReadOnlyList<JsonNode> reports)
    {
        static string E(JsonNode? value) => WebUtility.HtmlEncode(value?.ToString() ?? "");
        static int N(JsonNode? value) => value is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        var projects = reports.SelectMany(r => r["projects"]?.AsArray() ?? []).OfType<JsonObject>().ToArray();
        var html = new StringBuilder("<h2>get_context relevance by provider</h2>");
        var rows = projects
            .SelectMany(p => p["context_by"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .GroupBy(r => (Provider: E(r["provider"]), Capability: E(r["capability"]), Language: E(r["language"])))
            .Select(g =>
                (
                    g.Key,
                    Hits: g.Sum(r => N(r["hits"])),
                    Changed: g.Sum(r => N(r["changed"])),
                    Tier0: g.Sum(r => N(r["tier0"]))
                )
            )
            .OrderByDescending(r => r.Hits)
            .ToArray();
        html.Append(
            rows.Length is 0
                ? "<p>No relevance reported yet.</p>"
                : "<table><tr><th>Provider</th><th>Capability</th><th>Language</th><th>Hits</th><th>Changed</th><th>Rate</th><th>Top tier</th></tr>"
                    + string.Concat(
                        rows.Select(r =>
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"<tr><td>{r.Key.Provider}</td><td>{r.Key.Capability}</td><td>{r.Key.Language}</td><td>{r.Hits}</td><td>{r.Changed}</td><td>{(r.Changed > 0 ? (double)r.Hits / r.Changed : 0):P0}</td><td>{r.Tier0}</td></tr>"
                            )
                        )
                    )
                    + "</table>"
        );
        html.Append("<h2>Tools</h2>");
        var tools = projects
            .SelectMany(p => p["tools"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .GroupBy(t => E(t["tool"]))
            .Select(g =>
                (
                    Tool: g.Key,
                    Calls: g.Sum(t => N(t["calls"])),
                    Errors: g.Sum(t => N(t["errors"])),
                    P95: g.Max(t => N(t["p95_ms"]))
                )
            )
            .OrderByDescending(t => t.Calls)
            .ToArray();
        html.Append(
            tools.Length is 0
                ? "<p>No tool calls reported yet.</p>"
                : "<table><tr><th>Tool</th><th>Calls</th><th>Errors</th><th>p95 (worst)</th></tr>"
                    + string.Concat(
                        tools.Select(t =>
                            string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"<tr><td>{t.Tool}</td><td>{t.Calls}</td><td>{t.Errors}</td><td>{t.P95} ms</td></tr>"
                            )
                        )
                    )
                    + "</table>"
        );
        foreach (
            var (audience, title) in new[]
            {
                ("developer", "Findings for the harness developer"),
                ("user", "Findings users act on"),
            }
        )
        {
            html.Append("<h2>").Append(title).Append("</h2>");
            var findings = projects
                .SelectMany(p =>
                    (p["findings"]?.AsArray() ?? []).OfType<JsonObject>().Select(f => (Project: p, Finding: f))
                )
                .Where(x => (x.Finding["audience"]?.ToString() ?? "") == audience)
                .GroupBy(x =>
                    (Level: E(x.Finding["level"]), Area: E(x.Finding["area"]), Message: E(x.Finding["message"]))
                )
                .Select(g => (g.Key, Projects: g.Select(x => x.Project).Distinct().Count()))
                .OrderByDescending(f => f.Projects)
                .ToArray();
            html.Append(
                findings.Length is 0
                    ? "<p>None reported.</p>"
                    : "<table><tr><th>Level</th><th>Area</th><th>Finding</th><th>Projects</th></tr>"
                        + string.Concat(
                            findings.Select(f =>
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"<tr><td>{f.Key.Level}</td><td>{f.Key.Area}</td><td>{f.Key.Message}</td><td>{f.Projects}</td></tr>"
                                )
                            )
                        )
                        + "</table>"
            );
        }

        return html.ToString();
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = CompanionHost.Options(args, "dir", "addr", "lan", "token", "help", "h");
            if (options.ContainsKey("help") || options.ContainsKey("h"))
            {
                Console.WriteLine(
                    "Usage: harness-collect [--dir telemetry-reports] [--addr HOST:PORT] [--lan] [--token TOKEN]"
                );
                return 0;
            }
            var directory = options.GetValueOrDefault("dir", "telemetry-reports");
            await using var app = Create(
                directory,
                options.GetValueOrDefault("addr", ""),
                options.GetValueOrDefault("lan") is "true",
                options.GetValueOrDefault("token", "")
            );
            await app.StartAsync();
            Console.WriteLine(
                $"Collecting harness telemetry into {directory}\n  page      {UiHost.Address(app)}\n  machines: harness telemetry enable --endpoint {UiHost.Address(app)}v1/report\nStop with Ctrl-C."
            );
            await app.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception ex)
            when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("harness-collect: " + ex.Message);
            return 1;
        }
    }
}
