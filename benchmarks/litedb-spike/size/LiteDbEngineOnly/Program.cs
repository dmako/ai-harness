using LiteDB;
using LiteDB.Engine;

var path = Path.Combine(Path.GetTempPath(), "size-" + Environment.ProcessId + ".litedb");
using (ILiteEngine engine = new SharedEngine(new EngineSettings { Filename = path }))
{
    engine.Insert("t", [new BsonDocument { ["_id"] = "a", ["v"] = 1 }], BsonAutoId.ObjectId);
    var query = new Query { Select = BsonExpression.Create("$") };
    query.Where.Add(BsonExpression.Create("_id = @0", "a"));
    using var reader = engine.Query("t", query);
    Console.WriteLine(reader.Read() ? reader.Current["v"].AsInt32 : -1);
}

File.Delete(path);
