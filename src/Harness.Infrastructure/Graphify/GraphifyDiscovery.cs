using Harness.Core.Providers;

namespace Harness.Infrastructure.Graphify;

public sealed record GraphifyInput(bool AtRoot = false, string Path = "", IReadOnlyList<string>? Candidates = null)
{
    public string Describe() => (AtRoot, Path, Candidates?.Count ?? 0) switch
    {
        (true, _, _) => "solution at the repository root",
        (_, { Length: > 0 } path, _) => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? "project " + path + " (no solution found)" : "solution " + path,
        (_, _, > 0) => "several solutions below the root (" + string.Join(", ", Candidates!) + "); none chosen",
        _ => "no .sln, .slnx or .csproj at the root or two folders below it",
    };
}

public static class GraphifyDiscovery
{
    private static readonly HashSet<string> Skipped = new(["bin", "obj", "node_modules", "packages", "artifacts", "TestResults", "vendor"], StringComparer.Ordinal);
    public static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string Command()
    {
        var name = OperatingSystem.IsWindows() ? "graphify-csharp.exe" : "graphify-csharp";
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => System.IO.Path.Combine(directory, name)).FirstOrDefault(File.Exists);
        if (path is not null)
        {
            return System.IO.Path.GetFullPath(path);
        }

        var tool = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", name);
        return File.Exists(tool) ? tool : "graphify-csharp";
    }

    public static (string Command, IReadOnlyList<string> Watch) FromConfig(IReadOnlyDictionary<string, ProviderConfiguration> providers)
    {
        var command = Command();
        if (!providers.TryGetValue("graphify-csharp", out var provider))
        {
            return (command, []);
        }

        var i = 0;
        while (i < provider.Args.Length)
        {
            var argument = provider.Args[i++];
            if (argument is "--")
            {
                return (command, provider.Args[i..]);
            }

            if (argument is "--graphify" && i < provider.Args.Length)
            {
                command = provider.Args[i++];
            }
            else if (argument.StartsWith("--graphify=", StringComparison.Ordinal))
            {
                command = argument["--graphify=".Length..];
            }
        }
        return (command, []);
    }

    public static GraphifyInput FindInput(string root)
    {
        string[] entries;
        try
        { entries = Directory.GetFileSystemEntries(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(); }
        if (entries.Any(path => !Directory.Exists(path) && IsInput(path)))
        {
            return new(true);
        }

        List<string> solutions = [], projects = [];
        void Walk(string directory, int depth)
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (Directory.Exists(entry))
                    {
                        if (depth < 2 && KeepDirectory(entry))
                        {
                            Walk(entry, depth + 1);
                        }
                    }
                    else if (IsSolution(entry))
                    {
                        solutions.Add(System.IO.Path.GetRelativePath(root, entry).Replace('\\', '/'));
                    }
                    else if (System.IO.Path.GetExtension(entry).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        projects.Add(System.IO.Path.GetRelativePath(root, entry).Replace('\\', '/'));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Unreadable folders are not input candidates. */ }
        }
        foreach (var entry in entries.Where(Directory.Exists).Where(KeepDirectory))
        {
            Walk(entry, 1);
        }

        var both = solutions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        solutions.RemoveAll(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && both.Contains(path + "x"));
        var found = (solutions.Count > 0 ? solutions : projects).Order(StringComparer.Ordinal).ToArray();
        return found.Length switch { 0 => new(), 1 => new(Path: found[0]), _ => new(Candidates: found) };
    }

    public static (IReadOnlyList<string> Arguments, string Description) WatchArguments(string root, IReadOnlyList<string> arguments)
    {
        if (arguments.Any(argument => argument is "--input" || argument.StartsWith("--input=", StringComparison.Ordinal)))
        {
            return (arguments.ToArray(), "solution from the provider's args");
        }

        var input = FindInput(root);
        if (input.AtRoot)
        {
            return (arguments.ToArray(), input.Describe());
        }

        if (input.Path.Length > 0)
        {
            return ([.. arguments, "--input", System.IO.Path.Combine(root, input.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))], input.Describe());
        }

        var hint = input.Candidates is { Count: > 0 } ? ": set [providers.graphify-csharp] args = [\"--\", \"--input\", \"" + input.Candidates[0] + "\"] in .harness/config.toml" : "";
        throw new InvalidDataException(input.Describe() + hint);
    }

    public static string Canonical(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        try
        { return new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full; }
        catch (IOException) { return full; }
    }
    private static bool KeepDirectory(string path) => !System.IO.Path.GetFileName(path).StartsWith('.') && !Skipped.Contains(System.IO.Path.GetFileName(path)) && new DirectoryInfo(path).LinkTarget is null;
    private static bool IsSolution(string path) => System.IO.Path.GetExtension(path) is var extension && (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase));
    private static bool IsInput(string path) => IsSolution(path) || System.IO.Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase);
}
