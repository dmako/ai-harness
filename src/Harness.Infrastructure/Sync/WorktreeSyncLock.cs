namespace Harness.Infrastructure.Sync;

public static class WorktreeSyncLock
{
    public static async Task<FileStream> AcquireAsync(string gitDirectory, CancellationToken token = default)
    {
        // Never recreate a Git directory removed/pruned while a hook was queued.
        if (!Directory.Exists(gitDirectory))
        {
            throw new DirectoryNotFoundException("The worktree Git directory no longer exists.");
        }

        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            { return new FileStream(Path.Combine(gitDirectory, "harness-sync.lock"), options); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 11 or 32 or 33 or 35 && Directory.Exists(gitDirectory)) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
}
