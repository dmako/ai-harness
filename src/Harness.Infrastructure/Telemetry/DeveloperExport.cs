using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Core.Memory;
using Harness.Core.Reports;
using Harness.Core.Serialization;
using Harness.Core.Telemetry;

namespace Harness.Infrastructure.Telemetry;

public sealed class ExportState
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Token { get; set; } = "";
    public string Machine { get; set; } = "";
    public Dictionary<string, string> Projects { get; set; } = [];
    public DateTimeOffset? LastSent { get; set; }

    public bool Due(DateTimeOffset now) =>
        Enabled && Endpoint.Length > 0 && (LastSent is null || now - LastSent >= TimeSpan.FromDays(1));
}

/// <summary>Explicit, aggregate-only export. This class never starts providers or reads source files.</summary>
public sealed partial class DeveloperExport(string directory)
{
    [GeneratedRegex(
        @"(?:[A-Za-z]:)?(?:[~.]?[A-Za-z0-9_@.\-]*[/\\])+[A-Za-z0-9_@.\-]*[A-Za-z_][A-Za-z0-9_@.\-]*|[a-z]+://\S+",
        RegexOptions.CultureInvariant,
        1000
    )]
    private static partial Regex Paths { get; }

    [GeneratedRegex(@"\b(?:prj|mem|clm|wt|pp)_[0-9a-f]{8,}\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Ids { get; }

    public string StatePath => Path.Combine(directory, "telemetry.json");
    public string LogPath => Path.Combine(directory, "telemetry.log");

    public ExportState Load() =>
        File.Exists(StatePath)
            ? HarnessJson.Deserialize<ExportState>(
                File.ReadAllText(StatePath, new UTF8Encoding(false, true)),
                TelemetryJson.Options
            ) ?? throw new InvalidDataException("Invalid telemetry state.")
            : new();

    public async Task SaveAsync(ExportState state, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PrivateExportFiles.Write(temporary, HarnessJson.SerializeToUtf8Bytes(state, TelemetryJson.Options));
            token.ThrowIfCancellationRequested();
            File.Move(temporary, StatePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public static void Enable(ExportState state, string endpoint, string token)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Length is 0
            || uri.UserInfo.Length > 0
            || uri.Fragment.Length > 0
        )
        {
            throw new InvalidDataException("endpoint must be an http(s) URL without credentials or fragment");
        }
        if (token.Any(c => c is < ' ' or > '~'))
        {
            throw new InvalidDataException("token must contain printable ASCII characters");
        }
        state.Enabled = true;
        state.Endpoint = endpoint;
        state.Token = token;
        if (state.Machine.Length is 0)
        {
            state.Machine = RandomId("m_");
        }
    }

    public static string Clean(string text) =>
        Ids.Replace(Paths.Replace(CredentialRedactor.Redact(text), "[path]"), "[id]");

    public static JsonObject Build(
        ExportState state,
        IEnumerable<LocalReport> reports,
        JsonObject build,
        IReadOnlyDictionary<string, string> versions,
        DateTimeOffset now
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(build);
        JsonArray projects = [];
        foreach (var report in reports)
        {
            if (!state.Projects.TryGetValue(report.Id, out var id))
            {
                id = RandomId("p_");
                state.Projects[report.Id] = id;
            }
            var context = report.Context;
            projects.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["id"] = id,
                        ["context"] = HarnessJson.SerializeToNode(
                            new JsonObjectMap
                            {
                                ["answers"] = context.Calls,
                                ["hit_rate"] = context.HitRate,
                                ["code_hits"] = context.Hits,
                                ["code_changed"] = context.Changed,
                                ["tests"] = context.Tests,
                                ["callee_misses"] = context.CalleeMisses,
                                ["read_after_rate"] = context.AfterRead.HitRate,
                            }
                        ),
                        ["tools"] = HarnessJson.SerializeToNode(
                            report.Metrics.Tools.Select(t => new JsonObjectMap
                            {
                                ["tool"] = Clean(t.Tool),
                                ["kind"] = Clean(t.Kind ?? ""),
                                ["calls"] = t.Calls,
                                ["errors"] = t.Errors,
                                ["empty"] = t.Empty,
                                ["p50_ms"] = t.P50MS,
                                ["p95_ms"] = t.P95MS,
                                ["clients"] = t.Clients?.Select(Clean),
                            })
                        ),
                        ["providers"] = HarnessJson.SerializeToNode(
                            report.Metrics.Providers.Select(p => new JsonObjectMap
                            {
                                ["provider"] = Clean(p.Provider),
                                ["capability"] = Clean(p.Capability),
                                ["calls"] = p.Calls,
                                ["errors"] = p.Errors,
                                ["timeouts"] = p.Timeouts,
                                ["empty"] = p.Empty,
                                ["p50_ms"] = p.P50MS,
                                ["p95_ms"] = p.P95MS,
                                ["context_hit_share"] = p.HitShare,
                            })
                        ),
                        ["knowledge"] = HarnessJson.SerializeToNode(
                            new JsonObjectMap
                            {
                                ["current_memories"] = report.Knowledge.Current,
                                ["proposed"] = report.Knowledge.Proposed,
                                ["approved"] = report.Knowledge.Approved,
                                ["rejected"] = report.Knowledge.Rejected,
                                ["pending"] = report.Knowledge.Pending,
                                ["reads"] = report.KnowledgeReads,
                                ["writes"] = report.KnowledgeWrites,
                            }
                        ),
                        ["hook_events"] = report.Metrics.Hooks.Events,
                        ["languages"] = HarnessJson.SerializeToNode(context.Language),
                        ["findings"] = HarnessJson.SerializeToNode(
                            (report.Findings ?? []).Select(f => new JsonObjectMap
                            {
                                ["level"] = Clean(f.Level),
                                ["area"] = Clean(f.Area),
                                ["message"] = Clean(f.Message),
                            })
                        ),
                    }
            );
        }
        return new()
        {
            ["schema"] = 1,
            ["machine"] = state.Machine,
            ["sent_at"] = now.UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture
            ),
            ["harness"] = build.DeepClone(),
            ["window_days"] = 30,
            ["provider_versions"] = HarnessJson.SerializeToNode(
                versions.ToDictionary(p => Clean(p.Key), p => Clean(p.Value))
            ),
            ["projects"] = projects,
        };
    }

    public async Task SendAsync(ExportState state, byte[] body, DateTimeOffset now, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Enabled)
        {
            throw new InvalidOperationException("the export is off");
        }
        var sent = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CheckCertificateRevocationList = true,
            };
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Post, state.Endpoint)
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.UserAgent.ParseAdd("harness/0.1");
            if (state.Token.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.Token);
            }
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException("collector answered " + (int)response.StatusCode);
            }
            state.LastSent = now.ToUniversalTime();
            sent = true;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(LogPath))
                {
                    PrivateExportFiles.Write(LogPath, []);
                }
                PrivateExportFiles.OwnerOnly(LogPath);
                await File.AppendAllTextAsync(
                        LogPath,
                        HarnessJson.Serialize(
                            new JsonObjectMap
                            {
                                ["at"] = now,
                                ["bytes"] = body.Length,
                                ["ok"] = sent,
                                ["error"] = sent ? null : "collector request failed",
                            }
                        ) + "\n",
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* Logging cannot change send success. */
            }
        }
    }

    private static string RandomId(string prefix) =>
        prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
