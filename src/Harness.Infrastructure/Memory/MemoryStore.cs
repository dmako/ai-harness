using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Harness.Core.Memory;
using Harness.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Memory;

/// <summary>Compatible memory operations over a caller-owned database; one connection per operation.</summary>
public sealed partial class MemoryStore(HarnessDatabase database, TimeProvider? timeProvider = null) : IMemoryStore
{
    private const string Columns =
        "m.id, m.type, m.content, m.scope_kind, m.scope_value, m.source_type, m.source_ref, m.created_by, m.confidence, COALESCE(m.superseded_by, ''), COALESCE(m.supersede_reason, ''), m.valid_from, m.valid_until, m.created_at, m.updated_at";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public MemoryPutResult Put(MemoryInput input, CancellationToken cancellationToken = default)
    {
        var normalized = MemoryText.Validate(input);
        return database.WithTransaction(
            (connection, transaction) => Put(connection, transaction, normalized, cancellationToken),
            cancellationToken
        );
    }

    public MemoryPutResult Supersede(string oldId, MemoryInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var old = Get(oldId, cancellationToken);
        return Put(
            input with
            {
                Type = input.Type.Length is 0 ? old.Type : input.Type,
                Scope = input.Scope is null or { Kind: "" } ? old.Scope : input.Scope,
                Supersedes = [oldId],
                Force = true,
            },
            cancellationToken
        );
    }

    public MemoryRecord Retire(string id, string by, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reason);
        reason = reason.Trim();
        if (reason.Length is 0)
        {
            throw new ArgumentException("a reason is required to retire a memory");
        }

        if (string.IsNullOrWhiteSpace(by))
        {
            by = "unknown";
        }

        return database.WithTransaction(
            (connection, transaction) =>
            {
                var current = Get(connection, transaction, id);
                var now = Now();
                if (!current.Current(now))
                {
                    throw new InvalidOperationException($"memory {id} is not current (superseded or expired)");
                }

                var stamp = now.ToString();
                Execute(
                    connection,
                    transaction,
                    "UPDATE memories SET valid_until=$stamp, updated_at=$stamp WHERE id=$id",
                    ("$stamp", stamp),
                    ("$id", id)
                );
                AddEvent(connection, transaction, id, "retired", "", by, reason, stamp);
                return Get(connection, transaction, id);
            },
            cancellationToken
        );
    }

    public MemoryRecord Get(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction((connection, transaction) => Get(connection, transaction, id), cancellationToken);

    public IReadOnlyList<MemoryRecord> History(string id, CancellationToken cancellationToken = default) =>
        database.ReadTransaction(
            (connection, transaction) =>
            {
                var memory = Get(connection, transaction, id);
                List<MemoryRecord> chain = [memory];
                HashSet<string> seen = new(StringComparer.Ordinal) { id };
                var current = id;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var previous = Command(
                        connection,
                        transaction,
                        "SELECT id FROM memories WHERE superseded_by=$id ORDER BY created_at DESC LIMIT 1",
                        ("$id", current)
                    );
                    if (previous.ExecuteScalar() is not string predecessor || !seen.Add(predecessor))
                    {
                        break;
                    }

                    chain.Insert(0, Get(connection, transaction, predecessor));
                    current = predecessor;
                }

                var next = memory.SupersededBy;
                while (next.Length is not 0 && seen.Add(next))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var successor = Get(connection, transaction, next);
                    chain.Add(successor);
                    next = successor.SupersededBy;
                }

                return (IReadOnlyList<MemoryRecord>)chain;
            },
            cancellationToken
        );

    public IReadOnlyList<MemoryRecord> Search(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var fullText = MemoryText.FullTextQuery(query.Text);
        if (fullText.Length is 0 && !string.IsNullOrWhiteSpace(query.Text))
        {
            return [];
        }

        var limit = query.Limit <= 0 ? 20 : query.Limit;
        var scoped = query.Scope is not null && string.IsNullOrWhiteSpace(query.Text);
        List<string> where = [];
        List<(string Name, object? Value)> parameters = [];
        if (fullText.Length is not 0)
        {
            where.Add("memories_fts MATCH $fts");
            parameters.Add(("$fts", fullText));
        }

        if (query.Types.Count is not 0)
        {
            List<string> names = [];
            for (var index = 0; index < query.Types.Count; index++)
            {
                var name = "$type" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                names.Add(name);
                parameters.Add((name, query.Types[index]));
            }

            where.Add("m.type IN (" + string.Join(',', names) + ")");
        }

        if (!query.IncludeInactive)
        {
            where.Add(
                "m.superseded_by IS NULL AND (m.valid_from IS NULL OR m.valid_from <= $now) AND (m.valid_until IS NULL OR m.valid_until > $now)"
            );
            parameters.Add(("$now", Now().ToString()));
        }

        var from = fullText.Length is 0 ? "memories m" : "memories_fts f JOIN memories m ON m.rowid=f.rowid";
        var order = fullText.Length is 0 ? "m.updated_at DESC" : "bm25(memories_fts)";
        var sql =
            "SELECT "
            + Columns
            + " FROM "
            + from
            + (where.Count is 0 ? "" : " WHERE " + string.Join(" AND ", where))
            + " ORDER BY "
            + order;
        using var connection = database.OpenConnection();
        using var command = Command(connection, null, sql, [.. parameters]);
        using var reader = command.ExecuteReader();
        List<MemoryRecord> output = [];
        while ((scoped || output.Count < limit) && reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var memory = Read(reader);
            if (
                query.Scope is not null
                && !memory.Scope.AppliesTo(query.Scope)
                && !(query.Branch.Length is not 0 && memory.Scope == new MemoryScope("branch", query.Branch))
            )
            {
                continue;
            }

            output.Add(memory);
        }

        IEnumerable<MemoryRecord> sorted = output;
        if (scoped)
        {
            sorted = output.OrderByDescending(memory => memory.Scope.Specificity);
        }

        return sorted.Take(limit).ToArray();
    }

    public IReadOnlyList<SimilarMemory> Similar(
        string type,
        string content,
        CancellationToken cancellationToken = default
    ) =>
        database.ReadTransaction(
            (connection, transaction) => FindSimilar(connection, transaction, type, content, Now(), cancellationToken),
            cancellationToken
        );

    public IReadOnlyList<MemoryEvent> Events(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = database.OpenConnection();
        return Events(connection, null, id, cancellationToken);
    }

    public MemoryDetail Details(string id, bool history = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var memory = Get(id.Trim(), cancellationToken);
        return new MemoryDetail(
            memory,
            memory.Current(Now()),
            history ? History(memory.Id, cancellationToken) : [],
            history ? Events(memory.Id, cancellationToken) : []
        );
    }

    public ApplyingMemories Applying(string scope, string branch = "", CancellationToken cancellationToken = default)
    {
        var target = MemoryScope.Parse(scope);
        var query = new MemoryQuery
        {
            Scope = target,
            Branch = branch,
            Limit = 100,
        };
        return new ApplyingMemories(
            target.ToString(),
            branch,
            Search(query with { Types = ["goal"] }, cancellationToken),
            Search(query with { Types = ["constraint"] }, cancellationToken),
            Search(query with { Types = ["decision", "convention", "warning"] }, cancellationToken)
        );
    }

    private MemoryPutResult Put(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryInput input,
        CancellationToken cancellationToken
    )
    {
        var now = Now();
        var stamp = now.ToString();
        var hash = MemoryText.Hash(input.Content);
        var scope = input.Scope!;
        using (
            var duplicate = Command(
                connection,
                transaction,
                "SELECT id FROM memories WHERE type=$type AND scope_kind=$kind AND scope_value=$scope AND content_hash=$hash AND superseded_by IS NULL",
                ("$type", input.Type),
                ("$kind", scope.Kind),
                ("$scope", scope.Value),
                ("$hash", hash)
            )
        )
        {
            if (
                duplicate.ExecuteScalar() is string duplicateId
                && !input.Supersedes.Contains(duplicateId, StringComparer.Ordinal)
            )
            {
                Execute(
                    connection,
                    transaction,
                    "UPDATE memories SET updated_at=$stamp WHERE id=$id",
                    ("$stamp", stamp),
                    ("$id", duplicateId)
                );
                AddEvent(
                    connection,
                    transaction,
                    duplicateId,
                    "reaffirmed",
                    "",
                    input.Source.By,
                    input.Source.Ref,
                    stamp
                );
                return new MemoryPutResult("duplicate", Get(connection, transaction, duplicateId));
            }
        }

        foreach (var id in input.Supersedes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var old = Get(connection, transaction, id);
            if (old.SupersededBy.Length is not 0)
            {
                throw new InvalidOperationException(
                    $"{id} is already superseded by {old.SupersededBy}; supersede that one instead"
                );
            }
        }

        if (!input.Force && input.Supersedes.Count is 0)
        {
            var similar = FindSimilar(connection, transaction, input.Type, input.Content, now, cancellationToken);
            if (similar.Length is not 0)
            {
                return new MemoryPutResult("needs_decision") { Similar = similar };
            }
        }

        var newId = "mem_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        foreach (var id in input.Supersedes)
        {
            Execute(
                connection,
                transaction,
                "UPDATE memories SET superseded_by=$new, supersede_reason=$reason, updated_at=$stamp WHERE id=$id",
                ("$new", newId),
                ("$reason", NullIfEmpty(input.SupersedeReason)),
                ("$stamp", stamp),
                ("$id", id)
            );
            AddEvent(connection, transaction, id, "superseded", newId, input.Source.By, input.SupersedeReason, stamp);
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO memories(id, type, content, content_hash, scope_kind, scope_value, source_type, source_ref, created_by, confidence, valid_from, valid_until, created_at, updated_at)
            VALUES ($id, $type, $content, $hash, $kind, $scope, $source, $ref, $by, $confidence, $from, $until, $stamp, $stamp)
            """,
            ("$id", newId),
            ("$type", input.Type),
            ("$content", input.Content),
            ("$hash", hash),
            ("$kind", scope.Kind),
            ("$scope", scope.Value),
            ("$source", input.Source.Type),
            ("$ref", input.Source.Ref),
            ("$by", input.Source.By),
            ("$confidence", input.Confidence),
            ("$from", input.ValidFrom?.ToString()),
            ("$until", input.ValidUntil?.ToString()),
            ("$stamp", stamp)
        );
        AddEvent(connection, transaction, newId, "created", "", input.Source.By, input.Source.Ref, stamp);
        return new MemoryPutResult("created", Get(connection, transaction, newId))
        {
            Superseded = input.Supersedes.ToArray(),
        };
    }

    private static SimilarMemory[] FindSimilar(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string type,
        string content,
        MemoryTimestamp now,
        CancellationToken cancellationToken
    )
    {
        var fullText = MemoryText.FullTextQuery(content);
        if (fullText.Length is 0)
        {
            return [];
        }

        using var command = Command(
            connection,
            transaction,
            "SELECT "
                + Columns
                + """
                 FROM memories_fts f JOIN memories m ON m.rowid=f.rowid
                 WHERE memories_fts MATCH $fts AND m.type=$type AND m.superseded_by IS NULL
                   AND (m.valid_until IS NULL OR m.valid_until > $now)
                 ORDER BY bm25(memories_fts) LIMIT 25
                """,
            ("$fts", fullText),
            ("$type", type),
            ("$now", now.ToString())
        );
        using var reader = command.ExecuteReader();
        List<SimilarMemory> output = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var memory = Read(reader);
            var similarity = MemoryText.Similarity(content, memory.Content);
            if (similarity >= MemoryText.SimilarThreshold)
            {
                output.Add(new SimilarMemory(memory, similarity));
            }
        }

        return output.OrderByDescending(similar => similar.Similarity).Take(5).ToArray();
    }

    private static List<MemoryEvent> Events(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        CancellationToken cancellationToken
    )
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT event, COALESCE(related_id, ''), actor, COALESCE(detail, ''), at FROM memory_events WHERE memory_id=$id ORDER BY id",
            ("$id", id)
        );
        using var reader = command.ExecuteReader();
        List<MemoryEvent> output = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Add(
                new MemoryEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    ParseTime(reader.GetString(4)) ?? default
                )
            );
        }

        return output;
    }

    private static void AddEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string name,
        string related,
        string actor,
        string detail,
        string stamp
    ) =>
        Execute(
            connection,
            transaction,
            "INSERT INTO memory_events(memory_id, event, related_id, actor, detail, at) VALUES ($id, $event, $related, $actor, $detail, $stamp)",
            ("$id", id),
            ("$event", name),
            ("$related", NullIfEmpty(related)),
            ("$actor", actor),
            ("$detail", NullIfEmpty(detail)),
            ("$stamp", stamp)
        );

    private static MemoryRecord Get(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT " + Columns + " FROM memories m WHERE m.id=$id",
            ("$id", id)
        );
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : throw new KeyNotFoundException($"{id}: memory not found");
    }

    private static MemoryRecord Read(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            new MemoryScope(reader.GetString(3), reader.GetString(4)),
            new MemorySource(reader.GetString(5), reader.GetString(6), reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetDouble(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : ParseTime(reader.GetString(11)),
            reader.IsDBNull(12) ? null : ParseTime(reader.GetString(12)),
            ParseTime(reader.GetString(13)) ?? default,
            ParseTime(reader.GetString(14)) ?? default
        );

    private static MemoryTimestamp? ParseTime(string value)
    {
        try
        {
            return MemoryTimestamp.Parse(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private MemoryTimestamp Now() => new(_time.GetUtcNow());

    private static string? NullIfEmpty(string value) => value.Length is 0 ? null : value;

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Private callers construct SQL only from constant fragments and generated parameter names. All input values, including FTS expressions, are bound parameters."
    )]
    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters
    )
    {
        using var command = Command(connection, transaction, sql, parameters);
        command.ExecuteNonQuery();
    }
}
