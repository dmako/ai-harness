using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cli.Memory;
using Harness.Core.Reports;
using Harness.Core.Serialization;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;

namespace Harness.Cli.Telemetry;

internal class TelemetrySettings : InspectSettings
{
    public int Days { get; init; } = 30;
}

internal sealed class ReportSettings : TelemetrySettings
{
    public bool All { get; init; }
}

internal sealed class ReportCommand(ReportCommandRunner runner)
{
    public Task<int> ExecuteAsync(ReportSettings settings, CancellationToken cancellationToken) =>
        runner.RunAsync("report", settings, cancellationToken);
}

internal sealed class ReportCommandRunner(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    private static readonly IReadOnlyList<string> Tools = ReadTools();

    private static string[] ReadTools()
    {
        // Names come from embedded schemas; loading this view starts no provider.
        return typeof(Harness.Mcp.HarnessMcpServer)
            .Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Harness.Mcp.Schemas.", StringComparison.Ordinal))
            .Select(n => n["Harness.Mcp.Schemas.".Length..^5])
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<int> RunAsync(string verb, TelemetrySettings settings, CancellationToken token)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            return await (
                verb switch
                {
                    "report" => ReportAsync(settings, now, token),
                    _ => throw new InvalidOperationException("unknown report"),
                }
            );
        }
        catch (Exception ex) when (ReadError(ex))
        {
            await error.Writer.WriteLineAsync("harness " + verb + ": " + ex.Message);
            return 1;
        }
    }

    private async Task<int> ReportAsync(TelemetrySettings settings, DateTimeOffset now, CancellationToken token)
    {
        List<LocalReport> reports = [];
        if (settings is ReportSettings { All: true })
        {
            reports.AddRange(
                lifecycle.Registry.Load()["projects"]!
                    .AsArray()
                    .OfType<JsonObject>()
                    .Select(project => RegisteredReport(project, settings.Days, now))
            );
        }
        else
        {
            var location = await lifecycle.LocateAsync(settings.Project, cancellationToken: token);
            reports.Add(Read(location.DatabasePath, location.Root, settings.Days, now));
        }
        // What the user acts on; the developer's findings and tuning numbers go only to the opt-in telemetry.
        var own = reports.Select(r => r.ForUser()).ToArray();
        if (settings.Json)
        {
            await output.WriteLineAsync(
                HarnessJson.Serialize(new JsonObjectMap { ["projects"] = own }, TelemetryJson.Options)
            );
        }
        else
        {
            for (var i = 0; i < own.Length; i++)
            {
                if (i > 0)
                {
                    await output.WriteLineAsync();
                }

                await ReportText.ReportAsync(output, own[i]);
            }
        }

        return 0;
    }

    internal LocalReport RegisteredReport(JsonObject project, int days, DateTimeOffset now)
    {
        var root = project["root"]?.GetValue<string>() ?? "";
        try
        {
            return Read(project["db_path"]?.GetValue<string>() ?? "", root, days, now);
        }
        catch (Exception ex) when (ReadError(ex))
        {
            return new()
            {
                Id = project["id"]?.GetValue<string>() ?? "",
                Root = root,
                Days = days,
                Findings =
                [
                    new(
                        "problem",
                        ReportAudience.User,
                        "project",
                        "state cannot be read: " + ex.Message,
                        "harness doctor --project " + root
                    ),
                ],
            };
        }
    }

    private LocalReport Read(string path, string root, int days, DateTimeOffset now)
    {
        using var db = HarnessDatabase.OpenReadOnly(path);
        var report = new TelemetryStore(db).Report(
            days,
            now,
            Tools,
            ProviderServerMap.Read(
                root,
                ConfigurationLoader
                    .LoadFiles(lifecycle.GlobalConfigurationPath, ConfigurationPaths.ProjectPath(root))
                    .Value.Providers
            )
        );
        report.Root = root;
        return report;
    }

    private static bool ReadError(Exception e) =>
        e
            is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or Microsoft.Data.Sqlite.SqliteException
                or JsonException
                or ArgumentException
                or InvalidOperationException;
}
