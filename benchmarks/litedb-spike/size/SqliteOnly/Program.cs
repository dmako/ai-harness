using Microsoft.Data.Sqlite;

var path = Path.Combine(Path.GetTempPath(), "size-" + Environment.ProcessId + ".sqlite");
using (var connection = new SqliteConnection("Data Source=" + path))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t(k TEXT PRIMARY KEY, v INTEGER); INSERT INTO t VALUES('a', 1);";
    command.ExecuteNonQuery();
    command.CommandText = "SELECT v FROM t WHERE k = $k";
    command.Parameters.AddWithValue("$k", "a");
    Console.WriteLine(command.ExecuteScalar());
}

SqliteConnection.ClearAllPools();
File.Delete(path);
