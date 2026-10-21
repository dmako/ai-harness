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
    private static partial Regex MachineId();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The application stopping registration owns and disposes the semaphore.")]
    public static WebApplication Create(string directory, string address = "", bool lan = false, string token = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        var app = CompanionHost.Create(address, lan, 8766);
        var gate = new SemaphoreSlim(1);
        app.Lifetime.ApplicationStopped.Register(gate.Dispose);
        app.MapPost("/v1/report", async (HttpContext context) =>
        {
            if (token.Length > 0 && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()), Encoding.UTF8.GetBytes("Bearer " + token)))
            { return Results.Text("unauthorized\n", statusCode: 401); }
            using var body = new MemoryStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var buffer = new byte[8192];
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (body.Length + read > MaxBody)
                { return Results.Text("report too large\n", statusCode: 413); }
                body.Write(buffer, 0, read);
            }
            JsonNode? report;
            try
            { report = JsonNode.Parse(new UTF8Encoding(false, true).GetString(body.ToArray())); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or DecoderFallbackException)
            { return Results.Text("not a harness report (schema 1 with a machine id)\n", statusCode: 400); }
            if (report is not JsonObject obj || obj["schema"] is not JsonValue schema || !schema.TryGetValue<int>(out var version) || version is not 1 || obj["machine"] is not JsonValue machine || !machine.TryGetValue<string>(out var id) || !MachineId().IsMatch(id))
            { return Results.Text("not a harness report (schema 1 with a machine id)\n", statusCode: 400); }
            await gate.WaitAsync(timeout.Token);
            try
            {
                var dir = Path.Combine(directory, id);
                if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) is not 0)
                { return Results.StatusCode(400); }
                Directory.CreateDirectory(dir);
                if (!OperatingSystem.IsWindows())
                { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                var bytes = body.ToArray();
                var file = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) + ".json";
                PrivateExportFiles.Write(Path.Combine(dir, file), bytes);
                var temp = Path.Combine(dir, ".latest.tmp");
                PrivateExportFiles.Write(temp, bytes);
                File.Move(temp, Path.Combine(dir, "latest.json"), true);
            }
            finally
            { gate.Release(); }
            return Results.StatusCode(202);
        });
        app.MapMethods("/machines/{id}", ["GET", "HEAD"], (string id) =>
        {
            id = id.EndsWith(".json", StringComparison.Ordinal) ? id[..^5] : id;
            var file = Path.Combine(directory, id, "latest.json");
            return MachineId().IsMatch(id) && File.Exists(file) && !IsLink(file) && !IsLink(Path.GetDirectoryName(file)!) ? Results.Bytes(File.ReadAllBytes(file), "application/json") : Results.NotFound();
        });
        app.MapMethods("/", ["GET", "HEAD"], (HttpContext context) => CompanionHost.Html(context, "harness telemetry", MachinesPage(directory)));
        return app;
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) is not 0;
    private static string MachinesPage(string directory)
    {
        var html = new StringBuilder("<h1>harness telemetry</h1><p>Opt-in aggregated reports from harness machines. No code, paths, queries or project names.</p><table><tr><th>Machine</th><th>Last report</th><th>harness</th><th>Projects</th><th>Answers</th><th>Context hits</th><th>Findings</th><th>Reports</th></tr>");
        foreach (var dir in Directory.EnumerateDirectories(directory).Where(d => MachineId().IsMatch(Path.GetFileName(d)) && !IsLink(d)).OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "latest.json"))))
        {
            try
            {
                var file = Path.Combine(dir, "latest.json");
                if (IsLink(file))
                { continue; }
                var report = JsonNode.Parse(File.ReadAllBytes(file))!;
                var projects = report["projects"]?.AsArray() ?? [];
                var hits = projects.Sum(p => p?["context"]?["code_hits"]?.GetValue<int>() ?? 0);
                var changed = projects.Sum(p => p?["context"]?["code_changed"]?.GetValue<int>() ?? 0);
                var id = WebUtility.HtmlEncode(Path.GetFileName(dir));
                html.Append(System.Globalization.CultureInfo.InvariantCulture, $"<tr><td><a href=\"/machines/{id}.json\">{id}</a></td><td>{File.GetLastWriteTimeUtc(file):u}</td><td>{WebUtility.HtmlEncode(report["harness"]?["version"]?.ToString())}<br>{WebUtility.HtmlEncode(report["harness"]?["platform"]?.ToString())}</td><td>{projects.Count}</td><td>{projects.Sum(p => p?["context"]?["answers"]?.GetValue<int>() ?? 0)}</td><td>{(changed > 0 ? (double)hits / changed : 0):P0}</td><td>{projects.Sum(p => p?["findings"]?.AsArray().Count ?? 0)}</td><td>{Directory.GetFiles(dir, "2*.json").Length}</td></tr>");
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { /* Ignore incomplete reports. */ }
        }
        return html.Append("</table><p>On a machine: harness telemetry enable --endpoint http://&lt;this host&gt;:8766/v1/report</p>").ToString();
    }
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = CompanionHost.Options(args, "dir", "addr", "lan", "token", "help", "h");
            if (options.ContainsKey("help") || options.ContainsKey("h"))
            { Console.WriteLine("Usage: harness-collect [--dir telemetry-reports] [--addr HOST:PORT] [--lan] [--token TOKEN]"); return 0; }
            var directory = options.GetValueOrDefault("dir", "telemetry-reports");
            await using var app = Create(directory, options.GetValueOrDefault("addr", ""), options.GetValueOrDefault("lan") is "true", options.GetValueOrDefault("token", ""));
            await app.StartAsync();
            Console.WriteLine($"Collecting harness telemetry into {directory}\n  page      {UiHost.Address(app)}\n  machines: harness telemetry enable --endpoint {UiHost.Address(app)}v1/report\nStop with Ctrl-C.");
            await app.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        { await Console.Error.WriteLineAsync("harness-collect: " + ex.Message); return 1; }
    }
}
