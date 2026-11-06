using Community.LiteDB.Aot.Collections;
using Community.LiteDB.Aot.Context;
using Community.LiteDB.Aot.ModelBuilder;
using CommunityAot;
using LiteDB;

// The package's own path, the way its sample uses it: a generated mapper per entity behind a context.
var path = Path.Combine(Path.GetTempPath(), "community-aot-" + Environment.ProcessId + ".litedb");
var failures = 0;
var seen = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
try
{
    using var db = new FindingContext(path);
    Step("Insert via generated mapper", () =>
    {
        for (var i = 0; i < 10; i++)
        {
            db.Findings.Insert(new FindingEntity { Key = "key:" + i, Kind = i % 2 is 0 ? "cycle" : "complexity", Measure = i, Units = ["A", "B" + i], LastSeen = seen });
        }

        return "10 inserted";
    });
    Step("FindById (generated Deserialize)", () =>
    {
        var back = db.Findings.FindById("key:3");
        return back is { Kind: "complexity", Measure: 3 } && back.Units.SequenceEqual(["A", "B3"])
            ? $"{back.Key} {back.LastSeen:O} ({back.LastSeen.Kind})"
            : throw new InvalidOperationException("read " + back?.Key);
    });
    Step("Find string predicate", () =>
    {
        var found = db.Findings.Find("$.Kind = @0 AND $.Measure > @1", "cycle", 3).ToList();
        return found.Count == 3 ? "3 found" : throw new InvalidOperationException("" + found.Count);
    });
    Step("Transaction rollback", () =>
    {
        db.BeginTrans();
        db.Findings.DeleteMany("$.Kind = 'cycle'");
        db.Rollback();
        return db.Findings.FindAll().Count() == 10 ? "rolled back" : throw new InvalidOperationException();
    });
}
catch (Exception error)
{
    Console.WriteLine("FAIL  open: " + error.GetType().Name + ": " + error.Message);
    failures++;
}
finally
{
    File.Delete(path);
}

Console.WriteLine($"{failures} failure(s); {(typeof(object).Assembly.Location.Length is 0 ? "Native AOT" : "JIT")}");
return failures;

void Step(string name, Func<string> step)
{
    try
    {
        Console.WriteLine("PASS  " + name + ": " + step());
    }
    catch (Exception error)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {error.GetType().Name}: {error.Message}");
    }
}

namespace CommunityAot
{
    public sealed class FindingEntity
    {
        public string Key { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Measure { get; set; }
        public List<string> Units { get; set; } = [];
        public DateTime LastSeen { get; set; }
    }

    public partial class FindingContext(string filename) : LiteDbContext(filename)
    {
        public AotLiteCollection<FindingEntity> Findings => Collection<FindingEntity>();

        protected override void OnModelCreating(EntityModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FindingEntity>(entity =>
            {
                entity.HasKey(x => x.Key);
                entity.Property(x => x.Kind).HasIndex("idx_kind");
                entity.ToCollection("findings");
            });
        }
    }
}
