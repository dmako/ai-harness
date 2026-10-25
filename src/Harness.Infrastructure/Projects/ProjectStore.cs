using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Harness.Core.Projects;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Projects;

public static class ProjectStore
{
    public static ProjectRegistration Register(
        HarnessDatabase database,
        GitRepository repository,
        GitHead head,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(head);
        return database.WithTransaction(
            (connection, transaction) =>
            {
                var project = Load(connection, transaction);
                var created = project is null;
                var moved = false;
                var copied = false;
                var previous = "";
                var stamp = HarnessDatabase.FormatTime(DateTimeOffset.UtcNow);
                if (project is null)
                {
                    project = new ProjectIdentity(NewId("prj"), repository.StateRoot, database.Path, "", stamp);
                    Execute(
                        connection,
                        transaction,
                        "INSERT INTO projects(id, root_path, db_path, created_at, updated_at) VALUES ($id, $root, $db, $stamp, $stamp)",
                        ("$id", project.Id),
                        ("$root", project.Root),
                        ("$db", project.DatabasePath),
                        ("$stamp", stamp)
                    );
                }
                else if (
                    !GitRepository.PathEquals(project.Root, repository.StateRoot)
                    || !GitRepository.PathEquals(project.DatabasePath, database.Path)
                )
                {
                    previous = project.Root;
                    copied = File.Exists(project.DatabasePath) && !SameFile(project.DatabasePath, database.Path);
                    moved = !copied;
                    if (copied)
                    {
                        // A copied DB retains memory/tasks, but its per-worktree caches belong to the source.
                        Execute(
                            connection,
                            transaction,
                            "DELETE FROM files; DELETE FROM sync_state; DELETE FROM sync_runs; DELETE FROM worktrees;"
                        );
                        project = new ProjectIdentity(
                            NewId("prj"),
                            repository.StateRoot,
                            database.Path,
                            project.Id,
                            stamp
                        );
                    }
                    else
                    {
                        project = project with { Root = repository.StateRoot, DatabasePath = database.Path };
                    }

                    Execute(
                        connection,
                        transaction,
                        "UPDATE projects SET id=$id, root_path=$root, db_path=$db, copied_from=$copy, created_at=$created, updated_at=$stamp",
                        ("$id", project.Id),
                        ("$root", project.Root),
                        ("$db", project.DatabasePath),
                        ("$copy", project.CopiedFrom.Length == 0 ? DBNull.Value : project.CopiedFrom),
                        ("$created", project.CreatedAt),
                        ("$stamp", stamp)
                    );
                }

                using var find = Command(
                    connection,
                    transaction,
                    "SELECT id FROM worktrees WHERE git_dir=$dir",
                    ("$dir", repository.GitDir)
                );
                var id = find.ExecuteScalar() as string;
                var worktree = new WorktreeIdentity(
                    id ?? NewId("wt"),
                    repository.Root,
                    repository.GitDir,
                    repository.Linked,
                    head.Branch,
                    head.Commit,
                    head.Detached
                );
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO worktrees(id, project_id, git_dir, path, linked, branch, head_commit, detached, first_seen_at, last_seen_at)
                    VALUES ($id, $project, $dir, $path, $linked, $branch, $head, $detached, $stamp, $stamp)
                    ON CONFLICT(git_dir) DO UPDATE SET path=$path, linked=$linked, branch=$branch, head_commit=$head, detached=$detached, last_seen_at=$stamp
                    """,
                    ("$id", worktree.Id),
                    ("$project", project.Id),
                    ("$dir", worktree.GitDir),
                    ("$path", worktree.Path),
                    ("$linked", worktree.Linked),
                    ("$branch", worktree.Branch),
                    ("$head", worktree.Commit),
                    ("$detached", worktree.Detached),
                    ("$stamp", stamp)
                );
                return new ProjectRegistration(project, worktree, created, moved, copied, previous);
            },
            cancellationToken
        );
    }

    public static ProjectIdentity? Load(HarnessDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var connection = database.OpenConnection();
        return Load(connection, null);
    }

    public static IReadOnlyList<WorktreeIdentity> Worktrees(HarnessDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, git_dir, linked, branch, head_commit, detached FROM worktrees ORDER BY linked, path";
        using var reader = command.ExecuteReader();
        List<WorktreeIdentity> result = [];
        while (reader.Read())
        {
            result.Add(
                new WorktreeIdentity(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetBoolean(6)
                )
            );
        }

        return result;
    }

    private static ProjectIdentity? Load(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT id, root_path, db_path, copied_from, created_at FROM projects"
        );
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var copiedFrom = reader.IsDBNull(3) ? "" : reader.GetString(3);
        return new ProjectIdentity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            copiedFrom,
            reader.GetString(4)
        );
    }

    private static string NewId(string prefix) =>
        $"{prefix}_{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";

    private static bool SameFile(string first, string second)
    {
        // Resolve symbolic paths before deciding that a moved database is a copy.
        var original = File.ResolveLinkTarget(first, returnFinalTarget: true)?.FullName ?? first;
        var current = File.ResolveLinkTarget(second, returnFinalTarget: true)?.FullName ?? second;
        return GitRepository.PathEquals(original, current);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "All SQL callers are private and pass constant SQL; every data value is a bound parameter."
    )]
    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters
    )
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }
}
