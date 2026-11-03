using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Storage;

/// <summary>
/// Cheap fingerprints of a project database by kind of content (counts, last rowids, latest *_at values): harness
/// ui's live refresh compares them between polls, its view cache keys views by them.
/// </summary>
public static class DatabaseFingerprint
{
    /// <summary>What changes in a project database, by kind, and the tables that hold it.</summary>
    public static IReadOnlyList<(string Kind, string[] Tables, bool AppendOnly)> Kinds { get; } =
    [
        ("tasks", ["tasks", "task_events", "task_dependencies", "task_acceptance", "blueprints"], false),
        ("knowledge", ["memories", "memory_events"], false),
        ("proposals", ["memory_proposals", "finding_proposals", "task_proposals", "blueprint_proposals"], false),
        ("findings", ["findings", "finding_snapshots", "finding_marks"], false),
        ("activity", ["tool_calls", "provider_calls", "context_calls", "context_outcomes", "tool_events"], true),
        ("sync", ["sync_state", "sync_runs", "files", "worktrees"], false),
    ];

    /// <summary>
    /// A fingerprint of everything views read from a project database (all kinds but activity), for harness ui's
    /// view cache; an unreadable database gives a value that never matches.
    /// </summary>
    public static string Of(string path)
    {
        if (path.Length is 0 || !File.Exists(path))
        {
            return "none";
        }

        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 1,
                }.ToString()
            );
            connection.Open();
            var prints = Prints(connection, Probes(connection));
            return string.Join(
                "|",
                prints
                    .Where(print => print.Key is not "activity")
                    .OrderBy(print => print.Key, StringComparer.Ordinal)
                    .Select(print => print.Key + "=" + print.Value)
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Queries use a fixed table allowlist and quoted schema column identifiers; no request values enter SQL."
    )]
    public static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static Dictionary<string, string> Prints(SqliteConnection connection, Dictionary<string, string> queries) =>
        queries.ToDictionary(
            p => p.Key,
            p => Convert.ToString(Scalar(connection, p.Value), CultureInfo.InvariantCulture) ?? "",
            StringComparer.Ordinal
        );

    /// <summary>One query per kind: counts, last rowids and the latest *_at value of its tables.</summary>
    public static Dictionary<string, string> Probes(SqliteConnection connection)
    {
        Dictionary<string, string> queries = new(StringComparer.Ordinal);
        foreach (var kind in Kinds)
        {
            List<string> parts = [];
            foreach (var table in kind.Tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM pragma_table_info($table)";
                command.Parameters.AddWithValue("$table", table);
                using var reader = command.ExecuteReader();
                List<string> columns = [];
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
                if (columns.Count is 0)
                {
                    continue;
                }
                var expression = new StringBuilder(
                    kind.AppendOnly ? "COALESCE(MAX(rowid), 0)" : "COUNT(*) || ':' || COALESCE(MAX(rowid), 0)"
                );
                if (!kind.AppendOnly)
                {
                    foreach (var column in columns.Where(c => c.EndsWith("_at", StringComparison.Ordinal)))
                    {
                        expression
                            .Append(" || ':' || COALESCE(MAX(\"")
                            .Append(column.Replace("\"", "\"\"", StringComparison.Ordinal))
                            .Append("\"), '')");
                    }
                }
                parts.Add("(SELECT " + expression + " FROM " + table + ")");
            }
            if (parts.Count > 0)
            {
                queries[kind.Kind] = "SELECT " + string.Join(" || '|' || ", parts);
            }
        }

        return queries;
    }
}
