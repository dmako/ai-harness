using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Core.Architecture;
using Harness.Core.Findings;
using Harness.Core.Serialization;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Findings;

/// <summary>Schema-19 compatible snapshots and marks over a caller-owned database.</summary>
public sealed partial class FindingStore(HarnessDatabase database, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@":\d+\b", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LineNumber { get; }

    private string Stamp() => _clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only constant internal SQL fragments reach this helper; all values use parameters."
    )]
    internal static SqliteCommand Command(
        SqliteConnection c,
        SqliteTransaction? t,
        string sql,
        params (string Key, object Value)[] args
    )
    {
        var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (key, value) in args)
        {
            command.Parameters.AddWithValue(key, value);
        }

        return command;
    }

    internal static void Execute(
        SqliteConnection c,
        SqliteTransaction? t,
        string sql,
        params (string Key, object Value)[] args
    )
    {
        using var command = Command(c, t, sql, args);
        command.ExecuteNonQuery();
    }

    public static string Hash(ArchitectureFinding finding)
    {
        var evidence = (finding.Evidence ?? [])
            .Select(e => LineNumber.Replace(e, ""))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var text =
            "["
            + string.Join(',', new[] { finding.Kind, finding.Level, finding.Title, finding.Why }.Select(GoString))
            + ","
            + finding.Measure.ToString(CultureInfo.InvariantCulture)
            + ",["
            + string.Join(',', evidence.Select(GoString))
            + "]]";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 12));
    }

    private static string GoString(string value)
    {
        var result = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            result.Append(
                rune.Value switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '<' => "\\u003c",
                    '>' => "\\u003e",
                    '&' => "\\u0026",
                    0x2028 => "\\u2028",
                    0x2029 => "\\u2029",
                    < 0x20 => "\\u" + rune.Value.ToString("x4", CultureInfo.InvariantCulture),
                    _ => rune.ToString(),
                }
            );
        }

        return result.Append('"').ToString();
    }

    public static IReadOnlyList<ArchitectureFinding> Merge(IEnumerable<ArchitectureFinding> findings)
    {
        List<ArchitectureFinding> result = [];
        Dictionary<string, int> positions = new(StringComparer.Ordinal);
        foreach (var f in findings.Select(f => ArchitectureAnalyzer.Complete(f)))
        {
            if (positions.TryGetValue(f.Key, out var index))
            {
                var old = result[index];
                var evidence = (old.Evidence ?? []).Concat(f.Evidence ?? []).Distinct(StringComparer.Ordinal).ToArray();
                result[index] = old with
                {
                    Evidence = evidence,
                    Measure = Math.Max(Math.Max(old.Measure, f.Measure), evidence.Length),
                };
            }
            else
            {
                positions.Add(f.Key, result.Count);
                result.Add(f);
            }
        }

        return result;
    }

    public static int Severity(string level) =>
        level switch
        {
            "problem" => 2,
            "warn" => 1,
            _ => 0,
        };

    public static string Grew(FindingMark mark, ArchitectureFinding f) =>
        f switch
        {
            _ when Severity(f.Level) > Severity(mark.Level) => $"its level rose from {mark.Level} to {f.Level}",
            _ when (f.Measure >= mark.Measure + 2) && (2L * f.Measure >= 3L * mark.Measure) =>
                $"it grew from {mark.Measure} to {f.Measure}",
            _ => "",
        };

    internal sealed record Row(ArchitectureFinding Finding, string Digest, string Status, long Snapshot, string First);

    private static Dictionary<string, Row> Load(SqliteConnection c, SqliteTransaction? t)
    {
        using var command = Command(c, t, "SELECT key,finding,hash,status,snapshot,first_seen FROM findings");
        using var reader = command.ExecuteReader();
        Dictionary<string, Row> rows = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            var f =
                HarnessJson.Deserialize<ArchitectureFinding>(reader.GetString(1))
                ?? throw new InvalidDataException("invalid finding " + reader.GetString(0));
            rows.Add(
                reader.GetString(0),
                new(
                    ArchitectureAnalyzer.Complete(f),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5)
                )
            );
        }
        return rows;
    }

    private static Dictionary<string, FindingMark> Marks(SqliteConnection c, SqliteTransaction? t)
    {
        using var command = Command(
            c,
            t,
            "SELECT key,state,reason,level,measure,review_reason,memory_id,task_id,task_project,marked_by,marked_at,proposed_by FROM finding_marks"
        );
        using var r = command.ExecuteReader();
        Dictionary<string, FindingMark> marks = new(StringComparer.Ordinal);
        while (r.Read())
        {
            marks.Add(
                r.GetString(0),
                new()
                {
                    State = r.GetString(1),
                    Reason = r.GetString(2),
                    Level = r.GetString(3),
                    Measure = r.GetInt32(4),
                    ReviewReason = r.GetString(5),
                    MemoryId = r.GetString(6),
                    TaskId = r.GetString(7),
                    TaskProject = r.GetString(8),
                    MarkedBy = r.GetString(9),
                    MarkedAt = r.GetString(10),
                    ProposedBy = r.GetString(11),
                }
            );
        }

        return marks;
    }

    public FindingTake Take(string commit, IEnumerable<ArchitectureFinding> findings, CancellationToken token = default)
    {
        var found = Merge(findings);
        var now = Stamp();
        return database.WithTransaction(
            (c, t) =>
            {
                var current = Load(c, t);
                var live = found.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
                var added = found.Count(f => !current.TryGetValue(f.Key, out var old) || old.Status is "gone");
                var changed = found.Count(f =>
                    current.TryGetValue(f.Key, out var old) && old.Status is not "gone" && old.Digest != Hash(f)
                );
                var gone = current.Count(row => !live.Contains(row.Key) && row.Value.Status is not "gone");
                using var count = Command(c, t, "SELECT count(*) FROM finding_snapshots");
                if (
                    (added + changed + gone is 0)
                    && Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0
                )
                {
                    foreach (var f in found)
                    {
                        Execute(
                            c,
                            t,
                            "UPDATE findings SET finding=$data,last_seen=$now WHERE key=$key",
                            ("$data", HarnessJson.Serialize(f, Json)),
                            ("$now", now),
                            ("$key", f.Key)
                        );
                    }

                    return new FindingTake(null, [], found.Count);
                }
                Execute(
                    c,
                    t,
                    "INSERT INTO finding_snapshots(commit_sha,taken_at,found,added,changed,gone) VALUES($commit,$now,$found,$added,$changed,$gone)",
                    ("$commit", commit),
                    ("$now", now),
                    ("$found", found.Count),
                    ("$added", added),
                    ("$changed", changed),
                    ("$gone", gone)
                );
                using var identity = Command(c, t, "SELECT last_insert_rowid()");
                var id = (long)identity.ExecuteScalar()!;
                foreach (var f in found)
                {
                    token.ThrowIfCancellationRequested();
                    current.TryGetValue(f.Key, out var old);
                    var status = old switch
                    {
                        null or { Status: "gone" } => "new",
                        _ when old.Digest != Hash(f) => "changed",
                        _ => "unchanged",
                    };
                    var first = old is null or { Status: "gone" } ? now : old.First;
                    Execute(
                        c,
                        t,
                        """
                        INSERT INTO findings(key,kind,level,title,measure,finding,hash,status,snapshot,first_seen,last_seen)
                        VALUES($key,$kind,$level,$title,$measure,$data,$hash,$status,$id,$first,$now)
                        ON CONFLICT(key) DO UPDATE SET kind=excluded.kind,level=excluded.level,title=excluded.title,measure=excluded.measure,
                        finding=excluded.finding,hash=excluded.hash,status=excluded.status,snapshot=excluded.snapshot,first_seen=excluded.first_seen,last_seen=excluded.last_seen
                        """,
                        ("$key", f.Key),
                        ("$kind", f.Kind),
                        ("$level", f.Level),
                        ("$title", f.Title),
                        ("$measure", f.Measure),
                        ("$data", HarnessJson.Serialize(f, Json)),
                        ("$hash", Hash(f)),
                        ("$status", status),
                        ("$id", id),
                        ("$first", first),
                        ("$now", now)
                    );
                }
                foreach (
                    var (key, _) in current.Where(row => !live.Contains(row.Key) && row.Value.Status is not "gone")
                )
                {
                    Execute(
                        c,
                        t,
                        "UPDATE findings SET status='gone',snapshot=$id WHERE key=$key",
                        ("$id", id),
                        ("$key", key)
                    );
                }

                var marks = Marks(c, t);
                List<string> review = [];
                foreach (var f in found)
                {
                    if (
                        marks.TryGetValue(f.Key, out var mark)
                        && mark.State is "by_design"
                        && Grew(mark, f) is { Length: > 0 } why
                    )
                    {
                        Execute(
                            c,
                            t,
                            "UPDATE finding_marks SET state='review',review_reason=$why WHERE key=$key",
                            ("$why", why),
                            ("$key", f.Key)
                        );
                        review.Add(f.Key);
                    }
                }

                return new FindingTake(
                    new()
                    {
                        Id = id,
                        Commit = commit,
                        TakenAt = now,
                        Found = found.Count,
                        Added = added,
                        Changed = changed,
                        Gone = gone,
                    },
                    review.Order(StringComparer.Ordinal).ToArray(),
                    found.Count
                );
            },
            token
        );
    }

    private static FindingSnapshot? Latest(SqliteConnection c, SqliteTransaction? t)
    {
        using var command = Command(
            c,
            t,
            "SELECT id,commit_sha,taken_at,found,added,changed,gone FROM finding_snapshots ORDER BY id DESC LIMIT 1"
        );
        using var r = command.ExecuteReader();
        return r.Read()
            ? new()
            {
                Id = r.GetInt64(0),
                Commit = r.GetString(1),
                TakenAt = r.GetString(2),
                Found = r.GetInt32(3),
                Added = r.GetInt32(4),
                Changed = r.GetInt32(5),
                Gone = r.GetInt32(6),
            }
            : null;
    }

    public FindingSnapshot? Latest(CancellationToken token = default) => database.ReadTransaction(Latest, token);

    public (IReadOnlyList<Finding> Findings, FindingSnapshot? Snapshot) Current(CancellationToken token = default) =>
        Read(null, token);

    public (IReadOnlyList<Finding> Findings, FindingSnapshot? Snapshot) Annotate(
        IEnumerable<ArchitectureFinding> found,
        CancellationToken token = default
    ) => Read(Merge(found), token);

    private (IReadOnlyList<Finding> Findings, FindingSnapshot? Snapshot) Read(
        IReadOnlyList<ArchitectureFinding>? found,
        CancellationToken token
    ) =>
        database.ReadTransaction(
            (c, t) =>
            {
                var latest = Latest(c, t);
                var rows = Load(c, t);
                var marks = Marks(c, t);
                Finding AnnotateOne(ArchitectureFinding f)
                {
                    rows.TryGetValue(f.Key, out var row);
                    return new(f)
                    {
                        Status = row is not null && row.Status is not "gone" ? row.Status : null,
                        FirstSeen = row is not null && row.Status is not "gone" ? row.First : null,
                        Mark = marks.GetValueOrDefault(f.Key),
                    };
                }
                var result = (
                    found
                    ?? rows.Values.Where(r => r.Status is not "gone")
                        .Select(r => r.Finding)
                        .OrderByDescending(f => Severity(f.Level))
                        .ThenByDescending(f => f.Rank)
                        .ThenBy(f => f.Key, StringComparer.Ordinal)
                        .ToArray()
                )
                    .Select(AnnotateOne)
                    .ToList();
                if (found is not null && latest is not null)
                {
                    var live = found.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var (key, row) in rows.OrderBy(row => row.Key, StringComparer.Ordinal))
                    {
                        if (
                            !live.Contains(key)
                            && row.Status is "gone"
                            && (row.Snapshot == latest.Id || marks.GetValueOrDefault(key)?.State is "task")
                        )
                        {
                            result.Add(
                                new(row.Finding)
                                {
                                    Status = "gone",
                                    FirstSeen = row.First,
                                    Mark = marks.GetValueOrDefault(key),
                                }
                            );
                        }
                    }
                }
                return ((IReadOnlyList<Finding>)result, latest);
            },
            token
        );

    public FindingMark? MarkOf(string key, CancellationToken token = default) =>
        database.ReadTransaction((c, t) => Marks(c, t).GetValueOrDefault(key), token);

    public FindingMark Put(ArchitectureFinding f, FindingMark mark, CancellationToken token = default)
    {
        mark = mark with { Level = f.Level, Measure = f.Measure, MarkedAt = Stamp(), ReviewReason = "" };
        return database.WithTransaction(
            (c, t) =>
            {
                Execute(
                    c,
                    t,
                    """
                    INSERT INTO finding_marks(key,state,reason,level,measure,review_reason,memory_id,task_id,task_project,marked_by,marked_at,proposed_by)
                    VALUES($key,$state,$reason,$level,$measure,'',$memory,$task,$project,$by,$at,$proposed)
                    ON CONFLICT(key) DO UPDATE SET state=excluded.state,reason=excluded.reason,level=excluded.level,measure=excluded.measure,
                    review_reason='',memory_id=excluded.memory_id,task_id=excluded.task_id,task_project=excluded.task_project,marked_by=excluded.marked_by,marked_at=excluded.marked_at,proposed_by=excluded.proposed_by
                    """,
                    ("$key", f.Key),
                    ("$state", mark.State),
                    ("$reason", mark.Reason),
                    ("$level", mark.Level),
                    ("$measure", mark.Measure),
                    ("$memory", mark.MemoryId),
                    ("$task", mark.TaskId),
                    ("$project", mark.TaskProject),
                    ("$by", mark.MarkedBy),
                    ("$at", mark.MarkedAt),
                    ("$proposed", mark.ProposedBy)
                );
                return mark;
            },
            token
        );
    }

    public FindingMark? Remove(string key, CancellationToken token = default) =>
        database.WithTransaction(
            (c, t) =>
            {
                var mark = Marks(c, t).GetValueOrDefault(key);
                Execute(c, t, "DELETE FROM finding_marks WHERE key=$key", ("$key", key));
                return mark;
            },
            token
        );
}
