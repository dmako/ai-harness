using System.Globalization;
using System.Text;
using Harness.Core.Memory;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Findings;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Telemetry;

public sealed partial class TelemetryStore(HarnessDatabase database, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Now() => Stamp(_clock.GetUtcNow());

    internal static string Stamp(DateTimeOffset at) => HarnessDatabase.FormatTime(at);

    private static string Safe(string text, int bytes) => Clip(CredentialRedactor.Redact(text), bytes);

    private static string TaskText(string text)
    {
        var safe = CredentialRedactor.Redact(text);
        return Encoding.UTF8.GetByteCount(safe) > 2000 ? Clip(safe, 2000)[..^1] : safe;
    }

    internal static string Clip(string text, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= bytes)
        {
            return text;
        }

        var output = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > bytes)
            {
                break;
            }

            output.Append(rune);
            bytes -= rune.Utf8SequenceLength;
        }
        return output + "…";
    }

    private static void Execute(
        SqliteConnection c,
        SqliteTransaction t,
        string sql,
        params (string Key, object Value)[] args
    ) => FindingStore.Execute(c, t, sql, args);

    private static long Identity(SqliteConnection c, SqliteTransaction t)
    {
        using var command = FindingStore.Command(c, t, "SELECT last_insert_rowid()");
        return (long)command.ExecuteScalar()!;
    }

    public void RecordTool(ToolObservation e, CancellationToken token = default) =>
        database.WithTransaction(
            (c, t) =>
            {
                Execute(
                    c,
                    t,
                    "INSERT INTO tool_calls(at,tool,kind,client,duration_ms,response_bytes,empty,error) VALUES($at,$tool,$kind,$client,$ms,$bytes,$empty,$error)",
                    ("$at", Stamp(e.At)),
                    ("$tool", e.Tool),
                    ("$kind", e.Kind),
                    ("$client", e.Client),
                    ("$ms", e.DurationMs),
                    ("$bytes", e.Bytes),
                    ("$empty", e.Empty),
                    ("$error", e.Error.Length > 0 ? Safe(e.Error, 500) : DBNull.Value)
                );
                var id = Identity(c, t);
                if (id % 100 is 0)
                {
                    Execute(c, t, "DELETE FROM tool_calls WHERE id <= $id", ("$id", id - 20000));
                }

                return 0;
            },
            token
        );

    public void RecordProvider(ProviderObservation e, CancellationToken token = default) =>
        database.WithTransaction(
            (c, t) =>
            {
                Execute(
                    c,
                    t,
                    "INSERT INTO provider_calls(at,provider,capability,query,items,response_bytes,duration_ms,error,timeout) VALUES($at,$provider,$capability,$query,$items,$bytes,$ms,$error,$timeout)",
                    ("$at", Stamp(e.At)),
                    ("$provider", e.Provider),
                    ("$capability", e.Capability),
                    ("$query", Safe(e.Query, 200)),
                    ("$items", e.Items),
                    ("$bytes", e.Bytes),
                    ("$ms", e.DurationMs),
                    ("$error", e.Error.Length > 0 ? Safe(e.Error, 1000) : DBNull.Value),
                    ("$timeout", e.Timeout)
                );
                var id = Identity(c, t);
                if (id % 100 is 0)
                {
                    Execute(c, t, "DELETE FROM provider_calls WHERE id <= $id", ("$id", id - 10000));
                }

                return 0;
            },
            token
        );

    public void RecordContext(ContextObservation e, CancellationToken token = default)
    {
        if (e.Head.Length is 0 || e.WorktreeId.Length is 0 || e.Units.Count is 0)
        {
            return;
        }

        database.WithTransaction(
            (c, t) =>
            {
                // SQLite's character CHECK also bounds multibyte text; clip bytes before insertion.
                Execute(
                    c,
                    t,
                    "INSERT INTO context_calls(at,worktree_id,head,task,budget,used) VALUES($at,$wt,$head,$task,$budget,$used)",
                    ("$at", Now()),
                    ("$wt", e.WorktreeId),
                    ("$head", e.Head),
                    ("$task", TaskText(e.Task)),
                    ("$budget", e.Budget),
                    ("$used", e.Used)
                );
                var id = Identity(c, t);
                foreach (var u in e.Units)
                {
                    Execute(
                        c,
                        t,
                        "INSERT INTO context_units(call_id,path,name,role,provider,capability,rank,position,tier,inlined,language) VALUES($id,$path,$name,$role,$provider,$capability,$rank,$position,$tier,$inlined,$language)",
                        ("$id", id),
                        ("$path", u.Path),
                        ("$name", u.Name),
                        ("$role", u.Role),
                        ("$provider", u.Provider),
                        ("$capability", u.Capability),
                        ("$rank", u.Rank),
                        ("$position", u.Position),
                        ("$tier", u.Tier),
                        ("$inlined", u.Inlined),
                        ("$language", Language(u.Path))
                    );
                }

                Execute(c, t, "DELETE FROM context_calls WHERE id <= (SELECT MAX(id) FROM context_calls)-2000");
                return 0;
            },
            token
        );
    }

    public void RecordEvent(ClientToolEvent e, CancellationToken token = default) =>
        database.WithTransaction(
            (c, t) =>
            {
                var paths = e.Paths is { Count: > 0 } ? e.Paths : [""];
                long last = 0;
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    Execute(
                        c,
                        t,
                        "INSERT INTO tool_events(at,client,session_id,worktree_id,tool,kind,mcp_server,mcp_tool,path) VALUES($at,$client,$session,$wt,$tool,$kind,$server,$mcp,$path)",
                        ("$at", Stamp(e.At)),
                        ("$client", e.Client),
                        ("$session", e.Session),
                        ("$wt", e.WorktreeId),
                        ("$tool", e.Tool),
                        ("$kind", e.Kind),
                        ("$server", e.McpServer),
                        ("$mcp", e.McpTool),
                        ("$path", path)
                    );
                    last = Identity(c, t);
                }
                if (e.Task.Length > 0 && e.Session.Length > 0)
                {
                    Execute(
                        c,
                        t,
                        """
                        UPDATE context_calls SET session_id=$session,client=$client WHERE id=(SELECT id FROM context_calls
                        WHERE worktree_id=$wt AND task=$task AND session_id IS NULL AND at >= $from ORDER BY id DESC LIMIT 1)
                        """,
                        ("$session", e.Session),
                        ("$client", e.Client),
                        ("$wt", e.WorktreeId),
                        ("$task", TaskText(e.Task)),
                        ("$from", Stamp(e.At.AddMinutes(-10)))
                    );
                }

                if (last / 100 != (last - paths.Count) / 100)
                {
                    Execute(c, t, "DELETE FROM tool_events WHERE id <= $id", ("$id", last - 50000));
                }

                return 0;
            },
            token
        );

    public static string Language(string path) =>
        Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".CS" or ".CSPROJ" or ".SLN" or ".SLNX" => "csharp",
            ".TS" or ".TSX" => "typescript",
            ".JS" or ".JSX" => "javascript",
            ".GO" => "go",
            ".PY" => "python",
            ".JAVA" => "java",
            ".KT" => "kotlin",
            ".RS" => "rust",
            ".SQL" => "sql",
            _ => "other",
        };
}
