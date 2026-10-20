using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Storage;

public static class DatabaseBackups
{
    public const int KeepBackups = 5;

    public static string DirectoryPath(string databasePath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "backups");

    public static IReadOnlyList<string> List(string databasePath)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(DirectoryPath(databasePath))
                .Where(path => Path.GetFileName(path).StartsWith("schema-", StringComparison.Ordinal) && path.EndsWith(".db", StringComparison.Ordinal))
                .OrderDescending(StringComparer.Ordinal).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static string Create(SqliteConnection connection, string databasePath, int version)
    {
        var directory = DirectoryPath(databasePath);
        StorageFiles.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".backup-{Guid.NewGuid():N}.tmp");
        string path;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "VACUUM INTO $path";
            command.Parameters.AddWithValue("$path", temporary);
            command.ExecuteNonQuery();
            StorageFiles.OwnerOnly(temporary);
            while (true)
            {
                var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss.fff'Z'", CultureInfo.InvariantCulture);
                var suffix = RandomNumberGenerator.GetInt32(65536).ToString("x4", CultureInfo.InvariantCulture);
                path = Path.Combine(directory, $"schema-{version:00}-{stamp}-{suffix}.db");
                try
                {
                    File.Move(temporary, path); // Publish without replacing another process's backup.
                    break;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // A four-hex suffix collided; choose another final name.
                }
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new IOException("Back up before migrating failed; no migrations were applied.", exception);
        }

        foreach (var old in List(databasePath).Skip(KeepBackups))
        {
            TryDelete(old);
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Another process can be pruning or inspecting the same backup.
        }
        catch (UnauthorizedAccessException)
        {
            // Match the source's best-effort pruning policy.
        }
    }
}
