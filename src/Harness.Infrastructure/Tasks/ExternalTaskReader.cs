using System.Text.Json.Nodes;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

/// <summary>
/// Reads a task of another project on this machine, for a dependency written &lt;project&gt;:&lt;task id&gt;. The
/// project comes from the machine registry by <see cref="ProjectNames"/>; its database is opened read-only, never
/// created or migrated.
/// </summary>
public sealed class ExternalTaskReader(Func<JsonObject> registry)
{
    /// <summary>The registry beside the global configuration this process uses (HARNESS_CONFIG_DIR or the default).</summary>
    public static ExternalTaskReader ForMachine(string? globalConfigurationPath = null) =>
        new(() =>
            new MachineRegistry(
                Path.Combine(
                    Path.GetDirectoryName(
                        Path.GetFullPath(globalConfigurationPath ?? ConfigurationPaths.GlobalPath())
                    )!,
                    "projects.json"
                )
            ).Load()
        );

    /// <summary>The task's status in the project named.</summary>
    /// <exception cref="ArgumentException">An unknown or ambiguous project, an unreadable one, or no such task.</exception>
    public string Status(string project, string id)
    {
        var found = ProjectNames.Find(registry(), project);
        var path = found["db_path"]?.GetValue<string>() ?? "";
        try
        {
            using var database = HarnessDatabase.OpenReadOnly(path);
            using var connection = database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM tasks WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string
                ?? throw new ArgumentException($"project {project} has no task {id}");
        }
        catch (Exception error) when (error is IOException or SqliteException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"project {project} cannot be read: {error.Message}", error);
        }
    }
}
