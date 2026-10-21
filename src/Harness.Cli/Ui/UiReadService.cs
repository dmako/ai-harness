using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Web;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Harness.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;

namespace Harness.Cli.Ui;

internal static class UiJson
{
    internal static readonly JsonSerializerOptions Options = new(Harness.Core.Telemetry.TelemetryJson.Options) { WriteIndented = false };
    internal static JsonNode? Node(object? value) => value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, Options);
}
internal sealed partial class UiReadService(ProjectLifecycle lifecycle, UiProjectCatalog catalog, UiSessions sessions) : IUiReadApi
{
    private WorkspaceStore Workspaces => new(lifecycle.GlobalConfigurationPath, lifecycle.Registry);
    private string GlobalDirectory => Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!;
    public async Task<JsonNode?> ReadAsync(UiReadRequest request, CancellationToken token)
    {
        try
        {
            if (request.Endpoint.StartsWith("/api/projects/{id}/", StringComparison.Ordinal))
            {
                return await ProjectAsync(request, token);
            }
            if (request.Endpoint.StartsWith("/api/workspaces/{name}/", StringComparison.Ordinal))
            {
                return await WorkspaceAsync(request, token);
            }
            return request.Endpoint switch
            {
                "/api/projects" => UiJson.Node(new { projects = await catalog.ListAsync(token) }),
                "/api/workspaces" => await WorkspacesAsync(token),
                "/api/workspaces/discover" => Discover(request),
                "/api/machine" => Machine(),
                "/api/search" => await SearchAsync(request, token),
                "/api/activity" => await ActivityAsync(request, token),
                "/api/stacks" => await StacksAsync(token),
                "/api/graphify/sessions" => await GraphifySessionsAsync(request, token),
                "/api/knowledge" or "/api/knowledge/memories/{mid}" => Knowledge(request, token),
                _ => throw new UiRequestException(404, "unknown endpoint"),
            };
        }
        catch (UiRequestException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new UiRequestException(400, exception.Message);
        }
        catch (SqliteException exception)
        {
            throw new UiRequestException(500, exception.Message);
        }
    }
    private static int Days(UiReadRequest request)
    {
        var value = request.Parameter("days");
        if (value.Length is 0)
        {
            return 30;
        }
        return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650 ? days : throw new UiRequestException(400, "days must be a whole number from 1 to 3650");
    }
    private static string[] Split(string value) => value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    private static void Trusted(UiReadRequest request, string message)
    {
        if (!request.Trusted)
        {
            throw new UiRequestException(403, message + "; it needs the X-Harness-UI: 1 header");
        }
    }
    private static bool ReadError(Exception exception) => exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or SqliteException or JsonException;
    private static HarnessDatabase Open(JsonObject project, bool tasks = false)
    {
        if (project["live"]?["state"]?.GetValue<string>() is not "ok")
        {
            throw new UiRequestException(409, "project " + UiProjectCatalog.Text(project, "id") + " is not readable (" + UiProjectCatalog.Text(project["live"]!, "state").Replace('_', ' ') + ")");
        }
        try
        {
            var path = UiProjectCatalog.Text(project, "db_path");
            return tasks ? HarnessDatabase.OpenExisting(path) : HarnessDatabase.OpenReadOnly(path);
        }
        catch (Exception exception) when (ReadError(exception))
        {
            throw new UiRequestException(409, exception.Message);
        }
    }
}
