using LiteDB;
using LiteDB.Engine;

namespace LiteDbSpike;

/// <summary>A finding as harness would map it with BsonMapper (reflection over public properties).</summary>
public sealed class FindingDoc
{
    [BsonId]
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Level { get; set; } = "";
    public int Measure { get; set; }
    public string[] Units { get; set; } = [];
    public DateTime LastSeen { get; set; }
    public MarkDoc? Mark { get; set; }
}

public sealed class MarkDoc
{
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>
/// Each way harness could talk to LiteDB, run under trimming/Native AOT: typed collections through BsonMapper (with
/// LINQ predicates, the mapping's documented path), manual BsonDocument mapping with Query/BsonExpression, and
/// LiteDB's SQL. Prints PASS or FAIL per step; the exit code counts failures.
/// </summary>
internal static class AotCheck
{
#if PRESERVE
    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(FindingDoc))]
    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(MarkDoc))]
#endif
    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "litedb-spike-aot-" + Environment.ProcessId);
        Directory.CreateDirectory(directory);
        var failures = 0;
        try
        {
            using var db = new LiteDatabase(
                new ConnectionString { Filename = Path.Combine(directory, "aot.db"), Connection = ConnectionType.Shared }
            );
            var seen = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

            failures += Step(
                "manual BsonDocument insert + Query.EQ/GT + index",
                () =>
                {
                    var col = db.GetCollection("manual");
                    col.EnsureIndex("kind");
                    for (var i = 0; i < 10; i++)
                    {
                        col.Upsert(
                            new BsonDocument
                            {
                                ["_id"] = "cycle:" + i,
                                ["kind"] = i % 2 is 0 ? "cycle" : "complexity",
                                ["measure"] = i,
                                ["units"] = new BsonArray(new BsonValue[] { "A", "B" + i }),
                                ["last_seen"] = seen,
                            }
                        );
                    }

                    var found = col.Find(Query.And(Query.EQ("kind", "cycle"), Query.GT("measure", 3))).ToList();
                    return found.Count == 3 && found.All(d => d["units"].AsArray.Count == 2)
                        ? $"{found.Count} documents"
                        : throw new InvalidOperationException("wrong answer: " + found.Count);
                }
            );

            failures += Step(
                "manual BsonExpression string predicate with parameter",
                () =>
                {
                    var col = db.GetCollection("manual");
                    var found = col.Find(BsonExpression.Create("$.kind = @0 AND $.measure >= @1", "complexity", 5))
                        .ToList();
                    return found.Count == 3 ? $"{found.Count} documents" : throw new InvalidOperationException("" + found.Count);
                }
            );

            failures += Step(
                "partial update: UpdateMany(transform, predicate)",
                () =>
                {
                    var col = db.GetCollection("manual");
                    var n = col.UpdateMany("{ status: 'gone' }", "$.kind = 'cycle'");
                    return n == 5 && col.Count("$.status = 'gone'") == 5
                        ? $"{n} updated"
                        : throw new InvalidOperationException("" + n);
                }
            );

            failures += Step(
                "aggregate: SQL SELECT COUNT/MAX GROUP BY",
                () =>
                {
                    using var reader = db.Execute(
                        "SELECT { kind: @key, n: COUNT(*), top: MAX(*.measure) } FROM manual GROUP BY $.kind"
                    );
                    var rows = new List<string>();
                    while (reader.Read())
                    {
                        rows.Add(reader.Current.ToString());
                    }

                    return rows.Count == 2 ? string.Join(" ", rows) : throw new InvalidOperationException(rows.Count + "");
                }
            );

            failures += Step(
                "typed GetCollection<FindingDoc>().Insert (BsonMapper reflection)",
                () =>
                {
                    var col = db.GetCollection<FindingDoc>("typed");
                    col.Insert(
                        new FindingDoc
                        {
                            Key = "cycle:A|B",
                            Kind = "cycle",
                            Level = "warn",
                            Measure = 4,
                            Units = ["A", "B"],
                            LastSeen = seen,
                            Mark = new MarkDoc { State = "by_design", Reason = "ok" },
                        }
                    );
                    var raw = db.GetCollection("typed").FindById("cycle:A|B");
                    return raw is not null && raw["Kind"] == "cycle" && raw["Mark"]["State"] == "by_design"
                        ? raw.ToString()
                        : throw new InvalidOperationException("stored " + raw);
                }
            );

            failures += Step(
                "typed FindById (BsonMapper deserialize)",
                () =>
                {
                    var doc = db.GetCollection<FindingDoc>("typed").FindById("cycle:A|B");
                    return doc is { Kind: "cycle", Measure: 4, Units: ["A", "B"], Mark.State: "by_design" }
                        ? $"{doc.Key} {doc.Level} {doc.LastSeen:O}"
                        : throw new InvalidOperationException("read " + (doc?.Kind ?? "null"));
                }
            );

            failures += Step(
                "typed LINQ predicate Find(x => x.Kind == \"cycle\" && x.Measure > 3)",
                () =>
                {
                    var found = db.GetCollection<FindingDoc>("typed").Find(x => x.Kind == "cycle" && x.Measure > 3)
                        .ToList();
                    return found.Count == 1 ? "1 document" : throw new InvalidOperationException("" + found.Count);
                }
            );

            failures += Step(
                "typed EnsureIndex(x => x.Kind)",
                () => db.GetCollection<FindingDoc>("typed").EnsureIndex(x => x.Kind) ? "created" : "existed"
            );

            foreach (var length in new[] { 100, 250, 400, 900, 1100 })
            {
                failures += Step(
                    $"ORDER BY a {length}-character string (finding keys reach 1.3 KB)",
                    () =>
                    {
                        var col = db.GetCollection("long" + length);
                        for (var i = 0; i < 3; i++)
                        {
                            col.Insert(new BsonDocument { ["_id"] = i, ["key"] = new string((char)('a' + i), length) });
                        }

                        var sorted = col.Query().OrderBy("key").ToList();
                        return sorted.Count == 3 ? "sorted" : throw new InvalidOperationException("" + sorted.Count);
                    }
                );
            }

            failures += Step(
                "transaction BeginTrans/Rollback",
                () =>
                {
                    var col = db.GetCollection("manual");
                    db.BeginTrans();
                    col.DeleteAll();
                    db.Rollback();
                    return col.Count() == 10 ? "rolled back" : throw new InvalidOperationException(col.Count() + "");
                }
            );

            db.Dispose();
            foreach (var shared in new[] { false, true })
            {
                failures += EngineSteps(Path.Combine(directory, shared ? "engine-shared.db" : "engine-direct.db"), shared);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL  open: " + error.GetType().Name + ": " + error.Message);
            failures++;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
        }

        Console.WriteLine(
            $"{failures} failure(s); LiteDB {typeof(LiteDatabase).Assembly.GetName().Version}; "
                + $"{Runtime.Mode}"
        );
        return failures;
    }

    /// <summary>Community.LiteDB.Aot's principles (EngineMapping.cs): generated mapper, ILiteEngine, string predicates.</summary>
    private static int EngineSteps(string path, bool shared)
    {
        var settings = new EngineSettings { Filename = path };
        using ILiteEngine engine = shared ? new SharedEngine(settings) : new LiteEngine(settings);
        var findings = new EngineCollection<FindingRecord>(engine, new FindingMapper());
        var label = shared ? "engine mapper (SharedEngine)" : "engine mapper (LiteEngine direct)";
        var seen = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        FindingRecord[] records =
        [
            .. Enumerable
                .Range(0, 10)
                .Select(i => new FindingRecord(
                    "key:" + i,
                    i % 2 is 0 ? "cycle" : "complexity",
                    "warn",
                    i,
                    ["A", "B" + i],
                    seen.AddMinutes(i),
                    i is 3 ? "by_design" : null
                )),
        ];
        var failures = 0;
        failures += Step(
            label + ": EnsureIndex + Upsert",
            () =>
            {
                findings.EnsureIndex("kind", "$.kind");
                return findings.Upsert(records) + " inserted";
            }
        );
        failures += Step(
            label + ": FindById round trip (records, lists, UTC time, null)",
            () =>
            {
                var back = findings.FindById("key:3")!;
                return back.Key == "key:3"
                    && back.Units.SequenceEqual(["A", "B3"])
                    && back.LastSeen == seen.AddMinutes(3)
                    && back.LastSeen.Offset == TimeSpan.Zero
                    && back.MarkState == "by_design"
                    && findings.FindById("key:4")!.MarkState is null
                    ? back.ToString()
                    : throw new InvalidOperationException("read " + back);
            }
        );
        failures += Step(
            label + ": string predicate with parameters + ORDER BY",
            () =>
            {
                var found = findings.Query("$.kind = @0 AND $.measure > @1", "measure", "cycle", 3);
                return found.Select(f => f.Measure).SequenceEqual([4, 6, 8])
                    ? string.Join(",", found.Select(f => f.Key))
                    : throw new InvalidOperationException(string.Join(",", found.Select(f => f.Measure)));
            }
        );
        failures += Step(
            label + ": UpdateMany + transaction rollback",
            () =>
            {
                engine.BeginTrans();
                var n = findings.UpdateMany("{ level: 'problem' }", "$.kind = 'cycle'");
                engine.Rollback();
                return n == 5 && findings.Find("$.level = 'problem'").Count is 0
                    ? "5 updated, rolled back"
                    : throw new InvalidOperationException("" + n);
            }
        );
        return failures;
    }

    private static int Step(string name, Func<string> step)
    {
        try
        {
            Console.WriteLine("PASS  " + name + ": " + step());
            return 0;
        }
        catch (Exception error)
        {
            var inner = error;
            while (inner.InnerException is not null)
            {
                inner = inner.InnerException;
            }

            Console.WriteLine($"FAIL  {name}: {error.GetType().Name}: {error.Message}"
                + (ReferenceEquals(inner, error) ? "" : $" <- {inner.GetType().Name}: {inner.Message}"));
            return 1;
        }
    }
}
