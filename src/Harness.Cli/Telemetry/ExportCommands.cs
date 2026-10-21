using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Cli.Memory;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Telemetry;
using Spectre.Console.Cli;

namespace Harness.Cli.Telemetry;

internal sealed class ExportSettings : CommandSettings
{
    [CommandOption("--endpoint")] public string Endpoint { get; init; } = "";
    [CommandOption("--token")] public string Token { get; init; } = "";
    [CommandOption("--auto")] public bool Auto { get; init; }
}
internal sealed class ExportCommandRunner(ProjectLifecycle lifecycle, ReportCommandRunner reports, BuildInformation build, TextWriter output, CliErrorOutput error)
{
    private DeveloperExport Export => new(Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!);
    internal async Task<int> RunAsync(string verb, ExportSettings settings, CancellationToken token)
    {
        try
        {
            var state = Export.Load();
            return await (verb switch
            {
                "status" => StatusAsync(state),
                "enable" => EnableAsync(state, settings, token),
                "disable" => DisableAsync(state, token),
                "preview" => PreviewAsync(state),
                "send" => SendAsync(state, settings.Auto, token),
                _ => throw new InvalidOperationException("unknown telemetry operation"),
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException or HttpRequestException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (settings.Auto)
            { return 0; }
            await error.Writer.WriteLineAsync("harness telemetry " + verb + ": " + (ex is HttpRequestException ? "collector request failed (logged; nothing else is affected)" : ex.Message));
            return 1;
        }
    }
    private async Task<int> StatusAsync(ExportState state)
    {
        await output.WriteLineAsync(state.Enabled ? $"Telemetry export: on\n  endpoint  {state.Endpoint}\n  machine   {state.Machine} (random id)\n  last sent {state.LastSent?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "never"} (at most daily, after a sync)\n  log       {Export.LogPath}" : "Telemetry export: off (nothing is sent). 'harness telemetry enable --endpoint URL' turns it on;\n'harness telemetry preview' shows what it would send.");
        return 0;
    }
    private async Task<int> EnableAsync(ExportState state, ExportSettings settings, CancellationToken token)
    {
        if (settings.Endpoint.Length is 0)
        { await error.Writer.WriteLineAsync("harness telemetry enable: --endpoint is required"); return 2; }
        DeveloperExport.Enable(state, settings.Endpoint, settings.Token);
        await Export.SaveAsync(state, token);
        await output.WriteLineAsync($"Telemetry export on: aggregated reports go to {settings.Endpoint} at most daily, after a sync.\n'harness telemetry preview' shows exactly what is sent; 'harness telemetry disable' stops it.");
        return 0;
    }
    private async Task<int> DisableAsync(ExportState state, CancellationToken token)
    {
        state.Enabled = false;
        await Export.SaveAsync(state, token);
        await output.WriteLineAsync("Telemetry export off: nothing more is sent.");
        return 0;
    }
    private async Task<int> PreviewAsync(ExportState state)
    {
        if (state.Machine.Length is 0)
        { state.Machine = "m_(assigned when enabled)"; }
        await output.WriteAsync(Encoding.UTF8.GetString(Payload(state, DateTimeOffset.UtcNow)));
        return 0;
    }
    private async Task<int> SendAsync(ExportState state, bool automatic, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (automatic && !state.Due(now))
        { return 0; }
        if (!state.Enabled)
        { await error.Writer.WriteLineAsync("harness telemetry send: the export is off ('harness telemetry enable --endpoint URL')"); return 2; }
        var body = Payload(state, now);
        try
        { await Export.SendAsync(state, body, now, token); }
        finally
        { await Export.SaveAsync(state, CancellationToken.None); }
        if (!automatic)
        { await output.WriteLineAsync($"Sent {body.Length} bytes to {state.Endpoint}."); }
        return 0;
    }
    private byte[] Payload(ExportState state, DateTimeOffset now)
    {
        var projects = lifecycle.Registry.Load()["projects"]!.AsArray().OfType<JsonObject>().ToArray();
        Dictionary<string, string> versions = [];
        foreach (var project in projects)
        {
            try
            {
                using var db = HarnessDatabase.OpenReadOnly(project["db_path"]!.GetValue<string>());
                using var connection = db.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, value FROM diagnostics WHERE key LIKE 'provider_version:%'";
                using var rows = command.ExecuteReader();
                while (rows.Read())
                { versions[rows.GetString(0)["provider_version:".Length..]] = rows.GetString(1); }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException) { /* Offline project. */ }
        }
        var body = DeveloperExport.Build(state, projects.Select(p => reports.RegisteredReport(p, 30, now)), new JsonObject { ["version"] = build.Version, ["commit"] = build.Commit, ["platform"] = build.Platform, ["go"] = build.Runtime }, versions, now);
        return Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
    internal void SendLater()
    {
        try
        {
            if (!Export.Load().Due(DateTimeOffset.UtcNow) || Environment.ProcessPath is not { } executable)
            { return; }
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            { start.ArgumentList.Add(typeof(CliApplication).Assembly.Location); }
            start.ArgumentList.Add("telemetry");
            start.ArgumentList.Add("send");
            start.ArgumentList.Add("--auto");
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { /* Sync never waits or fails for export. */ }
    }
}

internal sealed class ExportStatusCommand(ExportCommandRunner runner) : AsyncCommand<ExportSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ExportSettings settings, CancellationToken cancellationToken) => runner.RunAsync("status", settings, cancellationToken);
}
internal sealed class ExportEnableCommand(ExportCommandRunner runner) : AsyncCommand<ExportSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ExportSettings settings, CancellationToken cancellationToken) => runner.RunAsync("enable", settings, cancellationToken);
}
internal sealed class ExportDisableCommand(ExportCommandRunner runner) : AsyncCommand<ExportSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ExportSettings settings, CancellationToken cancellationToken) => runner.RunAsync("disable", settings, cancellationToken);
}
internal sealed class ExportPreviewCommand(ExportCommandRunner runner) : AsyncCommand<ExportSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ExportSettings settings, CancellationToken cancellationToken) => runner.RunAsync("preview", settings, cancellationToken);
}
internal sealed class ExportSendCommand(ExportCommandRunner runner) : AsyncCommand<ExportSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ExportSettings settings, CancellationToken cancellationToken) => runner.RunAsync("send", settings, cancellationToken);
}
