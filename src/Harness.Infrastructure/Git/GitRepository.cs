using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Harness.Infrastructure.Git;

public sealed record GitHead(string Branch, string Commit, bool Detached, bool Unborn)
{
    public override string ToString() => (Detached, Unborn) switch
    {
        (true, _) => $"detached at {ShortCommit}",
        (_, true) => $"{Branch} (no commits yet)",
        _ => $"{Branch} at {ShortCommit}",
    };
    private string ShortCommit => Commit.Length > 12 ? Commit[..12] : Commit;
}

public sealed record GitRepository(string Root, string GitDir, string CommonDir, bool Linked)
{
    public string StateRoot => Path.GetFileName(CommonDir) == ".git" ? Path.GetDirectoryName(CommonDir)! : Root;

    public static async Task<GitRepository> DiscoverAsync(string directory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(directory);
        var top = await RunAsync(root, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (top.Code != 0)
        {
            // Distinguish invalid/broken repositories from genuinely non-Git directories.
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            {
                if (Path.Exists(Path.Combine(ancestor.FullName, ".git")))
                {
                    throw new InvalidDataException($"Cannot discover Git repository: {top.Error.Trim()}");
                }
            }

            throw new NotRepositoryException(root);
        }

        root = Path.GetFullPath(top.Output.TrimEnd('\r', '\n'));
        var dirs = await RunAsync(root, ["rev-parse", "--path-format=absolute", "--git-dir", "--git-common-dir"], cancellationToken);
        if (dirs.Code != 0)
        {
            throw new InvalidDataException($"Cannot discover Git directories: {dirs.Error.Trim()}");
        }

        var paths = dirs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(path => Path.GetFullPath(path.TrimEnd('\r'))).ToArray();
        return new GitRepository(root, paths[0], paths[1], !PathEquals(paths[0], paths[1]));
    }

    public async Task<GitHead> ReadHeadAsync(CancellationToken cancellationToken = default)
    {
        var symbolic = await RunAsync(Root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        var commit = await RunAsync(Root, ["rev-parse", "--verify", "--quiet", "HEAD^{commit}"], cancellationToken);
        if (symbolic.Code == 0)
        {
            if (commit.Code is not (0 or 1))
            {
                throw new InvalidDataException("Cannot read Git HEAD.");
            }

            return new GitHead(symbolic.Output.TrimEnd('\r', '\n'), commit.Output.TrimEnd('\r', '\n'), false, commit.Code != 0);
        }

        return commit.Code == 0 && symbolic.Code == 1
            ? new GitHead("", commit.Output.TrimEnd('\r', '\n'), true, false)
            : throw new InvalidDataException("HEAD is neither a branch nor a commit.");
    }

    internal static bool PathEquals(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static async Task<(int Code, string Output, string Error)> RunAsync(string root, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(FindExecutable())
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
        {
            start.Environment.Remove(variable);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            try
            {
                process.Start();
                started = true;
                process.StandardInput.Close(); // A read-only Git command must never inherit the MCP protocol input pipe.
            }
            catch (Win32Exception exception)
            {
                throw new IOException("Git executable could not be started; install Git.", exception);
            }

            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Git discovery timed out.", exception);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static string FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var folder = directory.Trim('"');
            if (Path.IsPathFullyQualified(folder) && File.Exists(Path.Combine(folder, name)))
            {
                return Path.Combine(folder, name);
            }
        }

        throw new IOException("Git executable not found; install Git and add it to PATH.");
    }
}

public sealed class NotRepositoryException : IOException
{
    public NotRepositoryException() : base("not a git repository") { }
    public NotRepositoryException(string directory) : base($"not a git repository (or any parent directory): {directory}\nRun harness from inside a Git working tree, or create one with 'git init'.") { }
    public NotRepositoryException(string message, Exception innerException) : base(message, innerException) { }
}
