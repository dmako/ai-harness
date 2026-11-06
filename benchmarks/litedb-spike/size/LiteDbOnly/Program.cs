using LiteDB;

var path = Path.Combine(Path.GetTempPath(), "size-" + Environment.ProcessId + ".litedb");
using (var db = new LiteDatabase(new ConnectionString { Filename = path, Connection = ConnectionType.Shared }))
{
    var t = db.GetCollection("t");
    t.Insert(new BsonDocument { ["_id"] = "a", ["v"] = 1 });
    Console.WriteLine(t.FindOne(Query.EQ("_id", "a"))["v"].AsInt32);
}

File.Delete(path);
