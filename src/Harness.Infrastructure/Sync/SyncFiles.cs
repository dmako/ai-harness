using System.Security.Cryptography;
using System.Text;
using Harness.Core.Sync;
using Harness.Infrastructure.Storage;

namespace Harness.Infrastructure.Sync;

public static class SyncFiles
{
    public static FileStats Replace(HarnessDatabase database, string worktree, string root, IReadOnlyList<string> paths, CancellationToken token = default) =>
        Apply(database, worktree, root, paths, SyncStore.FileHashes(database, worktree).Keys.Except(paths, StringComparer.Ordinal).ToArray(), token);

    public static FileStats Apply(HarnessDatabase database, string worktree, string root, IEnumerable<string> candidates, IEnumerable<string> deleted, CancellationToken token = default) =>
        database.WithTransaction((connection, transaction) =>
        {
            List<string> added = [], changed = [], removed = [], gone = [.. deleted];
            var unchanged = 0;
            var hashed = 0;
            var stamp = HarnessDatabase.FormatTime(DateTimeOffset.UtcNow);
            foreach (var relative in candidates.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var path = SafePath(root, relative);
                using var find = SyncStore.Command(connection, transaction, "SELECT hash,size,mtime_ns FROM files WHERE worktree_id=$wt AND path=$path", ("$wt", worktree), ("$path", relative));
                string? previous = null;
                long previousSize = 0, previousTime = 0;
                using (var reader = find.ExecuteReader())
                    if (reader.Read())
                    { previous = reader.GetString(0); previousSize = reader.GetInt64(1); previousTime = reader.GetInt64(2); }
                var info = new FileInfo(path);
                FileAttributes attributes;
                try
                { attributes = File.GetAttributes(path); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { gone.Add(relative); continue; }
                var link = info.LinkTarget;
                if (link is null && attributes.HasFlag(FileAttributes.Directory))
                    continue;
                var size = link is null ? info.Length : Encoding.UTF8.GetByteCount(link);
                var mtime = checked((info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) * 100);
                if (link is null && previous is not null && previousSize == size && previousTime == mtime)
                { unchanged++; continue; }
                var hash = Hash(path, link, token);
                hashed++;
                if (previous is null)
                    added.Add(relative);
                else if (previous != hash)
                    changed.Add(relative);
                else
                    unchanged++;
                using var update = SyncStore.Command(connection, transaction, """
                    INSERT INTO files(worktree_id,path,hash,size,mtime_ns,updated_at) VALUES($wt,$path,$hash,$size,$mtime,$at)
                    ON CONFLICT(worktree_id,path) DO UPDATE SET hash=excluded.hash,size=excluded.size,mtime_ns=excluded.mtime_ns,updated_at=excluded.updated_at
                    """, ("$wt", worktree), ("$path", relative), ("$hash", hash), ("$size", size), ("$mtime", mtime), ("$at", stamp));
                update.ExecuteNonQuery();
            }
            foreach (var relative in gone.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                SafePath(root, relative);
                using var remove = SyncStore.Command(connection, transaction, "DELETE FROM files WHERE worktree_id=$wt AND path=$path", ("$wt", worktree), ("$path", relative));
                if (remove.ExecuteNonQuery() > 0)
                    removed.Add(relative);
            }
            return new FileStats(added, changed, removed, unchanged, hashed);
        }, token);

    private static string Hash(string path, string? link, CancellationToken token)
    {
        if (link is not null)
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("symlink:" + link)));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string SafePath(string root, string relative)
    {
        if (relative.Length is 0 || Path.IsPathRooted(relative) || relative.Split('/').Any(segment => segment is ".." or "." or "") || relative.Contains('\0', StringComparison.Ordinal) || OperatingSystem.IsWindows() && relative.Contains('\\', StringComparison.Ordinal))
            throw new InvalidDataException("Sync paths must be relative Git paths.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Sync path leaves the worktree.");
        for (var parent = Directory.GetParent(path); parent is not null && !Git.GitRepository.PathEquals(parent.FullName, root); parent = parent.Parent)
            if (parent.LinkTarget is not null)
                throw new InvalidDataException("Sync path traverses a directory link: " + relative);
        return path;
    }
}
