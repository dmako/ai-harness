using Harness.Core.Tasks;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Tasks;

/// <summary>
/// Dependencies on tasks of other projects, written &lt;project&gt;:&lt;task id&gt;: the other task's status is
/// read from its project and kept with the dependency, so readiness stays a query of this database; it is read again
/// once older than <see cref="ExternalTtl"/>.
/// </summary>
public sealed partial class TaskStore
{
    private static readonly TimeSpan ExternalTtl = TimeSpan.FromSeconds(30);

    private ExternalTaskReader? _machineTasks;

    private ExternalTaskReader ExternalTasks => externalTasks ?? (_machineTasks ??= ExternalTaskReader.ForMachine());

    /// <summary>The statuses of a definition's dependencies on other projects' tasks; a refused one names itself.</summary>
    private Dictionary<string, string> ExternalStatuses(TaskDefinition d)
    {
        Dictionary<string, string> statuses = new(StringComparer.Ordinal);
        foreach (var dependency in d.DependsOn)
        {
            if (!TaskWorkflow.External(dependency, out var project, out var id))
            {
                continue;
            }

            if (!TaskWorkflow.ValidId(id))
            {
                throw new ArgumentException(
                    $"{d.Id} depends on {dependency}: {TaskWorkflow.Quote(id)} is not a task id"
                );
            }

            try
            {
                statuses[dependency] = ExternalTasks.Status(project, id);
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException($"{d.Id} depends on {dependency}: {error.Message}", error);
            }
        }

        return statuses;
    }

    private void ReplaceExternal(SqliteConnection c, SqliteTransaction t, TaskDefinition d)
    {
        var statuses = ExternalStatuses(d);
        var stamp = Stamp();
        Execute(c, t, "DELETE FROM task_external_dependencies WHERE task_id=$id", ("$id", d.Id));
        foreach (var (dependency, status) in statuses)
        {
            TaskWorkflow.External(dependency, out var project, out var id);
            Execute(
                c,
                t,
                "INSERT INTO task_external_dependencies(task_id,project,depends_on,status,checked_at) VALUES($id,$project,$on,$status,$at)",
                ("$id", d.Id),
                ("$project", project),
                ("$on", id),
                ("$status", status),
                ("$at", stamp)
            );
        }
    }

    /// <summary>Reads again the statuses older than the TTL; a project that cannot be read keeps its last one.</summary>
    private void RefreshExternal(SqliteConnection c, SqliteTransaction t)
    {
        List<(string Project, string Id)> stale = [];
        using (
            var command = Command(
                c,
                t,
                "SELECT DISTINCT project,depends_on FROM task_external_dependencies WHERE checked_at IS NULL OR checked_at <= $before",
                ("$before", ExternalBefore())
            )
        )
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                stale.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var stamp = Stamp();
        foreach (var (project, id) in stale)
        {
            string status;
            try
            {
                status = ExternalTasks.Status(project, id);
            }
            catch (ArgumentException)
            {
                continue;
            }

            Execute(
                c,
                t,
                "UPDATE task_external_dependencies SET status=$status,checked_at=$at WHERE project=$project AND depends_on=$on",
                ("$status", status),
                ("$at", stamp),
                ("$project", project),
                ("$on", id)
            );
        }
    }

    private string ExternalBefore() =>
        Harness.Infrastructure.Storage.HarnessDatabase.FormatTime(_time.GetUtcNow() - ExternalTtl);
}
