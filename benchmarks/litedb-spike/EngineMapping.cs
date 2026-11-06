using LiteDB;
using LiteDB.Engine;

namespace LiteDbSpike;

/// <summary>
/// The principles of Community.LiteDB.Aot (github.com/mrdevrobot/Community-LiteDb-AOT), written by hand:
/// a mapper per type that converts to and from BsonDocument at compile time (what its source generator emits),
/// a typed collection over ILiteEngine directly (LiteDatabase, LiteCollection and BsonMapper are never touched, so
/// the trimmer can drop them), and string BsonExpression predicates with BsonValue parameters instead of LINQ lambdas.
/// Unlike Community.LiteDB.Aot's LiteDbContext (which always builds a direct LiteEngine), this can sit on
/// SharedEngine too, the only engine several harness processes could open.
/// </summary>
internal interface IEntityMapper<T>
{
    string CollectionName { get; }
    BsonDocument Serialize(T entity);
    T Deserialize(BsonDocument document);
}

/// <summary>A harness finding as an immutable record: what the stores would hand around.</summary>
internal sealed record FindingRecord(
    string Key,
    string Kind,
    string Level,
    int Measure,
    IReadOnlyList<string> Units,
    DateTimeOffset LastSeen,
    string? MarkState
);

/// <summary>What the generator would emit for FindingRecord: no reflection, no expression trees.</summary>
internal sealed class FindingMapper : IEntityMapper<FindingRecord>
{
    public string CollectionName => "findings";

    public BsonDocument Serialize(FindingRecord entity) =>
        new()
        {
            ["_id"] = entity.Key,
            ["kind"] = entity.Kind,
            ["level"] = entity.Level,
            ["measure"] = entity.Measure,
            ["units"] = new BsonArray(entity.Units.Select(unit => new BsonValue(unit))),
            // Stored as UTC ticks: a BsonDateTime comes back in local time (see the typed BsonMapper step).
            ["last_seen"] = entity.LastSeen.UtcTicks,
            ["mark_state"] = entity.MarkState is null ? BsonValue.Null : entity.MarkState,
        };

    public FindingRecord Deserialize(BsonDocument document) =>
        new(
            document["_id"].AsString,
            document["kind"].AsString,
            document["level"].AsString,
            document["measure"].AsInt32,
            [.. document["units"].AsArray.Select(unit => unit.AsString)],
            new DateTimeOffset(document["last_seen"].AsInt64, TimeSpan.Zero),
            document["mark_state"].IsNull ? null : document["mark_state"].AsString
        );
}

/// <summary>Community.LiteDB.Aot's AotLiteCollection, reduced to what harness's stores would call.</summary>
internal sealed class EngineCollection<T>(ILiteEngine engine, IEntityMapper<T> mapper)
{
    public int Upsert(IEnumerable<T> entities) =>
        engine.Upsert(mapper.CollectionName, entities.Select(mapper.Serialize), BsonAutoId.ObjectId);

    public T? FindById(BsonValue id) => Find("_id = @0", id).FirstOrDefault();

    public List<T> Find(string predicate, params BsonValue[] args) => Query(predicate, null, args);

    public List<T> Query(string? predicate, string? orderBy, params BsonValue[] args)
    {
        var query = new Query { Select = BsonExpression.Create("$") };
        if (predicate is not null)
        {
            query.Where.Add(BsonExpression.Create(predicate, args));
        }

        if (orderBy is not null)
        {
            query.OrderBy = BsonExpression.Create(orderBy);
            query.Order = LiteDB.Query.Ascending;
        }

        using var reader = engine.Query(mapper.CollectionName, query);
        List<T> found = [];
        while (reader.Read())
        {
            found.Add(mapper.Deserialize(reader.Current.AsDocument));
        }

        return found;
    }

    public int UpdateMany(string transform, string predicate) =>
        engine.UpdateMany(mapper.CollectionName, BsonExpression.Create(transform), BsonExpression.Create(predicate));

    public bool EnsureIndex(string name, string expression) =>
        engine.EnsureIndex(mapper.CollectionName, name, BsonExpression.Create(expression), false);
}
