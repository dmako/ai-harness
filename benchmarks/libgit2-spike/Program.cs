// TASK-0097 spike: harness's hot git commands run as a git process (like GitRepository.RunAsync) versus
// LibGit2Sharp in process, timed and checked for the same answer.
//
//   LibGit2Spike bench <repository> [runs]   time and compare each command on a repository
//   LibGit2Spike formats <empty folder>      build repositories in unusual formats and check libgit2 opens them
using System.Diagnostics;
using System.Text;
using LibGit2Sharp;

return args switch
{
    ["bench", var root, ..] => Bench.Run(Path.GetFullPath(root), args.Length > 2 ? int.Parse(args[2]) : 7),
    ["formats", var scratch] => Formats.Run(Path.GetFullPath(scratch)),
    ["exit", var root, var what] => ExitProbe(Path.GetFullPath(root), what),
    _ => Usage(),
};

// TASK-0098: one GitReader read on a fresh repository, disposed, then the process's handles and memory before it
// returns (a large repository's benchmark run took over 12 minutes to exit)
static int ExitProbe(string root, string what)
{
    var watch = Stopwatch.StartNew();
    using (var repo = Gr.Open(root))
    {
        var count = what switch
        {
            "churn" => Gr.Churn(repo, DateTimeOffset.UtcNow.AddDays(-180)).Count(),
            "status" => Gr.Dirty(repo).Count(),
            _ => Gr.RecentCommits(repo, [".gitignore"], 5).Count(),
        };
        Console.WriteLine($"{what}: {count} entries in {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    using var self = Process.GetCurrentProcess();
    Console.WriteLine(
        $"disposed; handles {self.HandleCount}, working set {self.WorkingSet64 / 1048576} MB, private {self.PrivateMemorySize64 / 1048576} MB; returning at {DateTime.Now:HH:mm:ss.fff}"
    );
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: LibGit2Spike bench <repository> [runs] | formats <empty folder>");
    return 2;
}

/// <summary>Runs git the way harness's GitRepository.RunAsync does.</summary>
static class Git
{
    private static readonly string[] Cleared =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
    ];

    public static (int Code, string Output) Run(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in Cleared)
        {
            start.Environment.Remove(name);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        _ = error.Result;
        return (process.ExitCode, output.Result);
    }

    public static string Read(string root, params string[] arguments)
    {
        var (code, output) = Run(root, arguments);
        return code == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: exit {code}");
    }

    public static string[] Nul(string text) => text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>One of harness's git commands and its in-process equivalent; both give a comparable answer.</summary>
sealed record Command(
    string Name,
    string Use,
    Func<string, IReadOnlyList<string>> ByGit,
    Func<Repository, IReadOnlyList<string>> ByLibGit2
);

static class Bench
{
    private static string N(string path) => path.Replace('\\', '/');

    private static IReadOnlyList<string> Sorted(IEnumerable<string> items) =>
        items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string Letter(ChangeKind kind) =>
        kind switch
        {
            ChangeKind.Added => "A",
            ChangeKind.Deleted => "D",
            ChangeKind.Modified => "M",
            ChangeKind.Renamed => "R",
            ChangeKind.Copied => "C",
            ChangeKind.TypeChanged => "T",
            _ => kind.ToString(),
        };

    private static string Change(TreeEntryChanges change) =>
        change.Status is ChangeKind.Renamed or ChangeKind.Copied
            ? $"{Letter(change.Status)} {N(change.OldPath)} -> {N(change.Path)}"
            : $"{Letter(change.Status)} {N(change.Path)}";

    /// <summary>Parses git's --name-status -z output (status, path, and a second path for renames and copies).</summary>
    private static IEnumerable<string> NameStatus(string[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            var letter = fields[i][..1];
            yield return letter is "R" or "C" ? $"{letter} {fields[++i]} -> {fields[++i]}" : $"{letter} {fields[++i]}";
        }
    }

    private static StatusOptions Untracked(bool renames) =>
        new()
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
            IncludeUnaltered = false,
            DetectRenamesInIndex = renames,
            DetectRenamesInWorkDir = false,
            ExcludeSubmodules = false,
        };

    /// <summary>Revisions and files picked by git so both sides answer the same question.</summary>
    private sealed record Picks(
        int Count,
        string Head,
        string Base,
        string From,
        string File,
        string[] Paths,
        DateTimeOffset Since
    );

    public static int Run(string root, int runs)
    {
        var picks = Pick(root);
        Console.WriteLine($"## {root}");
        Console.WriteLine(
            $"{picks.Count} commits; HEAD {picks.Head[..12]}; base HEAD~20 {picks.Base[..12]}; from HEAD~10 {picks.From[..12]}"
        );
        Console.WriteLine($"file {picks.File}; retrieval paths {string.Join(", ", picks.Paths)}");
        Console.WriteLine($"git {Git.Read(root, "version").Trim()}; libgit2 {GlobalSettings.Version}");

        Command[] commands =
        [
            FileList(),
            DirtyPaths(),
            CommitExists(picks),
            ChangesSince(picks),
            Churn(picks),
            RecentCommits(picks),
            ChangedBetween(picks),
            HunkHeaders(picks),
            FileAtCommit(picks),
            HooksPath(),
            KnowledgeIgnored(),
        ];
        var gr = ReaderCommands(commands, picks);

        var load = Stopwatch.StartNew();
        using var shared = new Repository(root);
        Console.WriteLine($"libgit2: native load + first open {load.Elapsed.TotalMilliseconds:F1} ms");
        var reopen = Median(
            runs,
            () =>
            {
                using var r = new Repository(root);
                _ = r.Head.Tip;
            }
        );
        Console.WriteLine($"libgit2: open + HEAD on a fresh Repository, median {reopen:F1} ms");
        load.Restart();
        using var reader = Gr.Open(root);
        _ = Gr.Head(reader);
        Console.WriteLine($"GitReader: first open + HEAD {load.Elapsed.TotalMilliseconds:F1} ms");
        var reopenReader = Median(
            runs,
            () =>
            {
                using var r = Gr.Open(root);
                _ = Gr.Head(r);
            }
        );
        Console.WriteLine($"GitReader: open + HEAD on a fresh repository, median {reopenReader:F1} ms");
        Console.WriteLine();
        Console.WriteLine(
            "| command | used by | git first | git median | libgit2 first | libgit2 median | libgit2 fresh repo | libgit2 same | GitReader first | GitReader median | GitReader fresh repo | GitReader same |"
        );
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---|---:|---:|---:|---|");
        foreach (var command in commands)
        {
            Row(root, runs, command, shared, reader, gr.GetValueOrDefault(command.Name));
        }

        return 0;
    }

    private static Picks Pick(string root)
    {
        var count = int.Parse(Git.Read(root, "rev-list", "--count", "HEAD").Trim());
        var head = Git.Read(root, "rev-parse", "HEAD").Trim();
        var baseRev = Git.Read(root, "rev-parse", $"HEAD~{Math.Min(20, count - 1)}").Trim();
        var from = Git.Read(root, "rev-parse", $"HEAD~{Math.Min(10, count - 1)}").Trim();
        var modified = Git.Nul(Git.Read(root, "diff", "--name-only", "-z", "--diff-filter=M", from, head, "--"));
        var file =
            modified.FirstOrDefault(p => p.EndsWith(".cs", StringComparison.Ordinal))
            ?? modified.FirstOrDefault()
            ?? Git.Nul(Git.Read(root, "ls-files", "-z")).First();
        var paths = modified.Take(3).DefaultIfEmpty(file).ToArray();
        return new(count, head, baseRev, from, file, paths, DateTimeOffset.UtcNow.AddDays(-180));
    }

    /// <summary>One command's table row: git, libgit2 and (when it has the command) GitReader.</summary>
    private static void Row(
        string root,
        int runs,
        Command command,
        Repository shared,
        GitReader.Primitive.PrimitiveRepository reader,
        Func<GitReader.Primitive.PrimitiveRepository, IReadOnlyList<string>>? byReader
    )
    {
        var (gitFirst, gitAnswer) = Timed(() => command.ByGit(root));
        var gitMedian = Median(runs, () => command.ByGit(root));
        var (libFirst, libAnswer) = Timed(() => command.ByLibGit2(shared));
        var libMedian = Median(runs, () => command.ByLibGit2(shared));
        var fresh = Median(
            Math.Min(runs, 3),
            () =>
            {
                using var r = new Repository(root);
                command.ByLibGit2(r);
            }
        );
        var readerCells = "— | — | — | no equivalent";
        if (byReader is not null)
        {
            var (grFirst, grAnswer) = Timed(() => byReader(reader));
            var grMedian = Median(runs, () => byReader(reader));
            var grFresh = Median(
                Math.Min(runs, 3),
                () =>
                {
                    using var r = Gr.Open(root);
                    byReader(r);
                }
            );
            readerCells = $"{grFirst:F1} | {grMedian:F1} | {grFresh:F1} | {Same(gitAnswer, grAnswer)}";
            Dump(command, "gitreader", grAnswer);
        }

        Dump(command, "libgit2", libAnswer);

        Console.WriteLine(
            $"| `{command.Name}` | {command.Use} | {gitFirst:F1} | {gitMedian:F1} | {libFirst:F1} | {libMedian:F1} | {fresh:F1} | {Same(gitAnswer, libAnswer)} | {readerCells} |"
        );
    }

    /// <summary>TASK-0098: the same commands through GitReader; a command missing here has no GitReader equivalent.</summary>
    private static Dictionary<string, Func<GitReader.Primitive.PrimitiveRepository, IReadOnlyList<string>>> ReaderCommands(
        Command[] commands,
        Picks picks
    ) =>
        new(StringComparer.Ordinal)
        {
            [commands[0].Name] = repo => Sorted(Gr.FileList(repo)),
            [commands[1].Name] = repo => Sorted(Gr.Dirty(repo)),
            [commands[2].Name] = repo =>
                [Gr.Commit(repo, GitReader.Hash.Parse(picks.Base)) is null ? "no" : "yes"],
            [commands[3].Name] = repo => Sorted(Gr.ChangesSince(repo, GitReader.Hash.Parse(picks.Base))),
            [commands[4].Name] = repo => [.. Gr.Churn(repo, picks.Since).Order(StringComparer.Ordinal)],
            [commands[5].Name] = repo => [.. Gr.RecentCommits(repo, picks.Paths, 5)],
            [commands[6].Name] = repo =>
                Sorted(Gr.ChangedBetween(repo, GitReader.Hash.Parse(picks.From), GitReader.Hash.Parse(picks.Head))),
            [commands[8].Name] = repo => [Gr.FileAt(repo, GitReader.Hash.Parse(picks.From), picks.File)],
        };

    private static Command FileList() =>
        new(
            "ls-files --cached --others --exclude-standard",
            "#4 file list (sync, every architecture read)",
            r => Sorted(Git.Nul(Git.Read(r, "ls-files", "-z", "--cached", "--others", "--exclude-standard"))),
            repo =>
                Sorted(
                    repo.Index.Select(e => N(e.Path))
                        .Concat(repo.RetrieveStatus(Untracked(false)).Untracked.Select(e => N(e.FilePath)))
                )
        );

    private static Command DirtyPaths() =>
        new(
            "status --porcelain=v1 -z -uall",
            "#5 dirty paths (every sync, coupling)",
            r =>
            {
                var fields = Git.Nul(Git.Read(r, "status", "--porcelain=v1", "-z", "--untracked-files=all"));
                var dirty = new List<string>();
                for (var i = 0; i < fields.Length; i++)
                {
                    dirty.Add(fields[i][3..]);
                    if (fields[i][0] is 'R' or 'C')
                    {
                        i++;
                    }
                }

                return Sorted(dirty);
            },
            repo =>
                Sorted(
                    repo.RetrieveStatus(Untracked(true))
                        .Where(e => e.State is not FileStatus.Unaltered and not FileStatus.Ignored)
                        .Select(e => N(e.FilePath))
                )
        );

    private static Command CommitExists(Picks picks) =>
        new(
            "cat-file -e <base>^{commit}",
            "#3 commit exists (2x per incremental sync)",
            r => [Git.Run(r, "cat-file", "-e", picks.Base + "^{commit}").Code == 0 ? "yes" : "no"],
            repo => [repo.Lookup<Commit>(picks.Base) is null ? "no" : "yes"]
        );

    private static Command ChangesSince(Picks picks) =>
        new(
            "diff --name-status -M <base> -- + ls-files --others",
            "#6+#7 changes since the last sync (incremental sync)",
            r =>
                Sorted(
                    NameStatus(
                            Git.Nul(
                                Git.Read(r, "diff", "--name-status", "-z", "-M", "--no-ext-diff", picks.Base, "--")
                            )
                        )
                        .Concat(
                            Git.Nul(Git.Read(r, "ls-files", "-z", "--others", "--exclude-standard"))
                                .Select(p => "? " + p)
                        )
                ),
            repo =>
            {
                // LibGit2Sharp's tree-to-work-tree diff includes untracked files as added: harness lists them apart
                var untracked = repo.RetrieveStatus(Untracked(false))
                    .Untracked.Select(e => N(e.FilePath))
                    .ToHashSet(StringComparer.Ordinal);
                using var changes = repo.Diff.Compare<TreeChanges>(
                    repo.Lookup<Commit>(picks.Base).Tree,
                    DiffTargets.Index | DiffTargets.WorkingDirectory,
                    null,
                    null,
                    new CompareOptions { Similarity = SimilarityOptions.Renames }
                );
                return Sorted(
                    changes
                        .Where(c => !(c.Status is ChangeKind.Added && untracked.Contains(N(c.Path))))
                        .Select(Change)
                        .Concat(untracked.Select(p => "? " + p))
                );
            }
        );

    private static Command Churn(Picks picks) =>
        new(
            "log --since=180d --no-merges --name-status -M",
            "#11 churn (every change_coupling call)",
            r => ChurnByGit(r, picks.Since),
            repo => ChurnByLibGit2(repo, picks.Since)
        );

    private static IReadOnlyList<string> ChurnByGit(string root, DateTimeOffset since)
    {
        var text = Git.Read(
            root,
            "-c",
            "core.quotePath=false",
            "log",
            "--no-merges",
            "--since=" + since.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            "--format=%x1e",
            "--name-status",
            "-M"
        );
        var entries = text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(commit =>
                commit
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Split('\t'))
                    .Select(f => f[0][0] is 'R' or 'C' ? $"{f[0][..1]} {f[1]} -> {f[2]}" : $"{f[0][..1]} {f[1]}")
            );
        return [.. entries.Order(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> ChurnByLibGit2(Repository repo, DateTimeOffset since)
    {
        var entries = new List<string>();
        var filter = new CommitFilter { IncludeReachableFrom = repo.Head.Tip, SortBy = CommitSortStrategies.Time };
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            if (commit.Committer.When < since)
            {
                break;
            }

            var parents = commit.Parents.ToArray();
            if (parents.Length > 1)
            {
                continue;
            }

            using var changes = repo.Diff.Compare<TreeChanges>(
                parents.FirstOrDefault()?.Tree,
                commit.Tree,
                new CompareOptions { Similarity = SimilarityOptions.Renames }
            );
            entries.AddRange(changes.Select(Change));
        }

        return [.. entries.Order(StringComparer.Ordinal)];
    }

    private static Command RecentCommits(Picks picks) =>
        new(
            "log -n 5 --no-merges -- <3 paths>",
            "#12 recent commits (get_context, cached per session)",
            r =>
                [
                    .. Git.Read(r, ["log", "-n", "5", "--no-merges", "--format=%H", "HEAD", "--", .. picks.Paths])
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries),
                ],
            repo => RecentCommitsByLibGit2(repo, picks.Paths)
        );

    private static IReadOnlyList<string> RecentCommitsByLibGit2(Repository repo, string[] paths)
    {
        var found = new List<string>();
        var filter = new CommitFilter { IncludeReachableFrom = repo.Head.Tip, SortBy = CommitSortStrategies.Time };
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            var parents = commit.Parents.ToArray();
            if (parents.Length > 1)
            {
                continue;
            }

            if (paths.Any(p => commit[p]?.Target.Id != parents.FirstOrDefault()?[p]?.Target.Id))
            {
                found.Add(commit.Sha);
                if (found.Count == 5)
                {
                    break;
                }
            }
        }

        return found;
    }

    private static Command ChangedBetween(Picks picks) =>
        new(
            "diff --name-only <from> <to>",
            "#13 telemetry resolve (per pending get_context)",
            r =>
                Sorted(
                    Git.Nul(
                        Git.Read(
                            r,
                            "diff",
                            "--name-only",
                            "-z",
                            "--no-renames",
                            "--no-ext-diff",
                            picks.From,
                            picks.Head,
                            "--"
                        )
                    )
                ),
            repo =>
            {
                using var changes = repo.Diff.Compare<TreeChanges>(
                    repo.Lookup<Commit>(picks.From).Tree,
                    repo.Lookup<Commit>(picks.Head).Tree
                );
                return Sorted(changes.Select(c => N(c.Path)));
            }
        );

    private static Command HunkHeaders(Picks picks) =>
        new(
            "diff -U0 <from> <to> -- <file> (hunk headers)",
            "#14 changed functions (up to 10 per resolved call)",
            r =>
                [
                    .. Git.Read(r, "diff", "-U0", "--no-color", "--no-ext-diff", picks.From, picks.Head, "--", picks.File)
                        .Split('\n')
                        .Where(l => l.StartsWith("@@", StringComparison.Ordinal)),
                ],
            repo =>
            {
                // Compare<Patch> kills the process under Native AOT (see README); blob-to-blob ContentChanges works
                var changes = repo.Diff.Compare(
                    (Blob)repo.Lookup<Commit>(picks.From)[picks.File].Target,
                    (Blob)repo.Lookup<Commit>(picks.Head)[picks.File].Target,
                    new CompareOptions { ContextLines = 0, InterhunkLines = 0 }
                );
                return [.. changes.Patch.Split('\n').Where(l => l.StartsWith("@@", StringComparison.Ordinal))];
            }
        );

    private static Command FileAtCommit(Picks picks) =>
        new(
            "show <from>:<file>",
            "#15 file at a commit (telemetry resolve)",
            r => [Git.Read(r, "show", picks.From + ":" + picks.File)],
            repo => [((Blob)repo.Lookup<Commit>(picks.From)[picks.File].Target).GetContentText()]
        );

    private static Command HooksPath() =>
        new(
            "config --get core.hooksPath",
            "#16 hooks folder (ui hooks endpoint)",
            r => [Git.Run(r, "config", "--get", "core.hooksPath").Output.Trim()],
            repo => [repo.Config.Get<string>("core.hooksPath")?.Value ?? ""]
        );

    private static Command KnowledgeIgnored() =>
        new(
            "check-ignore .harness/knowledge.toml",
            "#10 is the shared knowledge file ignored (ui, when it exists)",
            r =>
                [
                    Git.Run(r, "check-ignore", "-q", "--", ".harness/knowledge.toml").Code == 0
                        ? "ignored"
                        : "not ignored",
                ],
            repo => [repo.Ignore.IsPathIgnored(".harness/knowledge.toml") ? "ignored" : "not ignored"]
        );

    /// <summary>With LG2SPIKE_DUMP set to a folder, writes each library's full answer there (to compare AOT with JIT).</summary>
    private static void Dump(Command command, string library, IReadOnlyList<string> answer)
    {
        if (Environment.GetEnvironmentVariable("LG2SPIKE_DUMP") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var name = new string([.. command.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
            File.WriteAllLines(Path.Combine(folder, $"{library}-{name}.txt"), answer.Order(StringComparer.Ordinal));
        }
    }

    private static (double Ms, IReadOnlyList<string> Answer) Timed(Func<IReadOnlyList<string>> call)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var answer = call();
            return (watch.Elapsed.TotalMilliseconds, answer);
        }
        catch (Exception error)
        {
            return (watch.Elapsed.TotalMilliseconds, [$"!{error.GetType().Name}: {error.Message}"]);
        }
    }

    private static double Median(int runs, Action call)
    {
        var times = new List<double>();
        for (var i = 0; i < runs; i++)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                call();
            }
            catch (Exception)
            {
                // the first call reported it
            }

            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        return times[times.Count / 2];
    }

    private static string Same(IReadOnlyList<string> git, IReadOnlyList<string> lib)
    {
        if (git.SequenceEqual(lib, StringComparer.Ordinal))
        {
            return $"yes ({git.Count})";
        }

        var onlyGit = git.Except(lib, StringComparer.Ordinal).Take(2).Select(Short);
        var onlyLib = lib.Except(git, StringComparer.Ordinal).Take(2).Select(Short);
        return $"**no** git {git.Count} / library {lib.Count}; git only: {string.Join("; ", onlyGit)}; library only: {string.Join("; ", onlyLib)}";
    }

    private static string Short(string text)
    {
        var line = text.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);
        return line.Length > 80 ? line[..80] + "…" : line;
    }
}

/// <summary>Repositories in the formats git supports but harness may meet, opened by libgit2 and compared with git.</summary>
static class Formats
{
    private static void Sh(string cwd, params string[] arguments)
    {
        var (code, _) = Git.Run(cwd, arguments);
        if (code != 0)
        {
            throw new InvalidOperationException($"setup: git {string.Join(' ', arguments)} exited {code}");
        }
    }

    private static string Seed(string scratch, string name, params string[] init)
    {
        var root = Path.Combine(scratch, name);
        Directory.CreateDirectory(root);
        Sh(root, ["init", "-q", "-b", "main", .. init]);
        Sh(root, "config", "user.email", "spike@example.invalid");
        Sh(root, "config", "user.name", "spike");
        Sh(root, "config", "commit.gpgsign", "false");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "doc"));
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "class A {}\n");
        File.WriteAllText(Path.Combine(root, "doc", "b.md"), "# b\n");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "bin/\n");
        Sh(root, "add", "-A");
        Sh(root, "commit", "-q", "-m", "one");
        File.AppendAllText(Path.Combine(root, "src", "a.cs"), "class B {}\n");
        Sh(root, "commit", "-q", "-am", "two");
        File.WriteAllText(Path.Combine(root, "untracked.txt"), "x\n");
        return root;
    }

    public static int Run(string scratch)
    {
        Directory.CreateDirectory(scratch);
        Console.WriteLine($"git {Git.Read(scratch, "version").Trim()}; libgit2 {GlobalSettings.Version}");
        var cases = new List<(string Name, Func<string> Make)>
        {
            ("plain", () => Seed(scratch, "plain")),
            ("sha256 objects", () => Seed(scratch, "sha256", "--object-format=sha256")),
            ("reftable refs", () => Seed(scratch, "reftable", "--ref-format=reftable")),
            (
                "index v4",
                () =>
                {
                    var r = Seed(scratch, "index4");
                    Sh(r, "update-index", "--index-version", "4");
                    return r;
                }
            ),
            (
                "split index",
                () =>
                {
                    var r = Seed(scratch, "split");
                    Sh(r, "update-index", "--split-index");
                    return r;
                }
            ),
            (
                "untracked cache",
                () =>
                {
                    var r = Seed(scratch, "ucache");
                    Sh(r, "config", "core.untrackedCache", "true");
                    Sh(r, "update-index", "--untracked-cache");
                    Sh(r, "status", "--porcelain");
                    return r;
                }
            ),
            (
                "sparse checkout (cone)",
                () =>
                {
                    var r = Seed(scratch, "sparse");
                    Sh(r, "sparse-checkout", "set", "--cone", "src");
                    return r;
                }
            ),
            (
                "sparse index",
                () =>
                {
                    var r = Seed(scratch, "sparseindex");
                    Sh(r, "sparse-checkout", "set", "--cone", "--sparse-index", "src");
                    return r;
                }
            ),
            (
                "worktreeConfig extension",
                () =>
                {
                    var r = Seed(scratch, "wtconfig");
                    Sh(r, "config", "extensions.worktreeConfig", "true");
                    return r;
                }
            ),
            (
                "linked worktree",
                () =>
                {
                    var r = Seed(scratch, "main-for-wt");
                    Sh(r, "worktree", "add", "-q", Path.Combine(scratch, "linked"), "-b", "side");
                    return Path.Combine(scratch, "linked");
                }
            ),
            (
                "submodule",
                () =>
                {
                    var inner = Seed(scratch, "inner");
                    var outer = Seed(scratch, "outer");
                    Sh(outer, "-c", "protocol.file.allow=always", "submodule", "add", "-q", inner, "lib");
                    Sh(outer, "commit", "-q", "-m", "sub");
                    return outer;
                }
            ),
            (
                "shared clone (alternates)",
                () =>
                {
                    var src = Seed(scratch, "alt-src");
                    Sh(scratch, "clone", "-q", "--shared", src, "alternates");
                    return Path.Combine(scratch, "alternates");
                }
            ),
            (
                "shallow clone",
                () =>
                {
                    var src = Seed(scratch, "shallow-src");
                    Sh(scratch, "clone", "-q", "--depth", "1", "file://" + src.Replace('\\', '/'), "shallow");
                    return Path.Combine(scratch, "shallow");
                }
            ),
            (
                "partial clone (blob:none)",
                () =>
                {
                    var src = Seed(scratch, "partial-src");
                    Sh(src, "config", "uploadpack.allowFilter", "true");
                    Sh(
                        scratch,
                        "clone",
                        "-q",
                        "--filter=blob:none",
                        "--no-checkout",
                        "file://" + src.Replace('\\', '/'),
                        "partial"
                    );
                    var r = Path.Combine(scratch, "partial");
                    Sh(r, "checkout", "-q", "main");
                    return r;
                }
            ),
            (
                "core.autocrlf=true, CRLF work tree",
                () =>
                {
                    var r = Seed(scratch, "crlf");
                    Sh(r, "config", "core.autocrlf", "true");
                    File.WriteAllText(Path.Combine(r, "doc", "b.md"), "# b\r\n");
                    return r;
                }
            ),
            (
                "refs packed (gc)",
                () =>
                {
                    var r = Seed(scratch, "packed");
                    Sh(r, "gc", "-q");
                    return r;
                }
            ),
            (
                "commit-graph + multi-pack-index",
                () =>
                {
                    var r = Seed(scratch, "graph");
                    Sh(r, "repack", "-adq", "--write-midx");
                    Sh(r, "commit-graph", "write", "--reachable");
                    return r;
                }
            ),
        };

        Console.WriteLine(
            "| repository | library | opens | HEAD | file list (ls-files) | dirty paths (status) | old file (HEAD~1:src/a.cs) |"
        );
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (name, make) in cases)
        {
            string root;
            try
            {
                root = make();
            }
            catch (Exception error)
            {
                Console.WriteLine($"| {name} | | (git could not create it: {error.Message}) | | | | |");
                continue;
            }

            // both libraries read before git touches the repository again (git fetches a partial clone's blobs on demand)
            var libgit2 = CheckLibGit2(root);
            var reader = CheckGitReader(root);
            Console.WriteLine($"| {name} | libgit2 | {Finish(root, libgit2)} |");
            Console.WriteLine($"| | GitReader | {Finish(root, reader)} |");
        }

        return 0;
    }

    /// <summary>What a library answered: whether it opened, HEAD, file list, dirty paths and the old file.</summary>
    private sealed record Answers(
        string? Failure,
        Func<string>? Head,
        Func<IEnumerable<string>>? Files,
        Func<IEnumerable<string>>? Dirty,
        string Old
    );

    /// <summary>The old file's text, or '!' and why it could not be read.</summary>
    private static string Read(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception error)
        {
            return $"!**fails**: {error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}";
        }
    }

    private static string Try(Func<string> check)
    {
        try
        {
            return check();
        }
        catch (Exception error)
        {
            return $"**fails**: {error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}";
        }
    }

    private static string Compare(IEnumerable<string> git, IEnumerable<string> lib)
    {
        var g = git.Select(p => p.Replace('\\', '/')).Distinct().Order(StringComparer.Ordinal).ToArray();
        var l = lib.Select(p => p.Replace('\\', '/')).Distinct().Order(StringComparer.Ordinal).ToArray();
        return g.SequenceEqual(l)
            ? $"same ({g.Length})"
            : $"**differs**: git [{string.Join(", ", g)}] library [{string.Join(", ", l)}]";
    }

    private static string Finish(string root, Answers answers)
    {
        if (answers.Failure is { } failure)
        {
            return $"**no**: {failure} | | | |";
        }

        var head = Try(() =>
            Git.Read(root, "rev-parse", "HEAD").Trim() is var sha && sha == answers.Head!()
                ? "same"
                : $"**differs** ({answers.Head!()})"
        );
        var files = Try(() =>
            Compare(
                Git.Nul(Git.Read(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard")),
                answers.Files!()
            )
        );
        var dirty = Try(() =>
        {
            var fields = Git.Nul(Git.Read(root, "status", "--porcelain=v1", "-z", "--untracked-files=all"));
            return Compare(fields.Where(f => f.Length > 3 && f[2] == ' ').Select(f => f[3..]), answers.Dirty!());
        });
        // the library read the old file first; git now (it may fetch a partial clone's blob on demand)
        var old = answers.Old.StartsWith('!')
            ? answers.Old[1..]
            : Try(() => answers.Old == Git.Read(root, "show", "HEAD~1:src/a.cs") ? "same" : "**differs**");
        return $"yes | {head} | {files} | {dirty} | {old}";
    }

    private static Answers CheckLibGit2(string root)
    {
        Repository repo;
        try
        {
            repo = new Repository(root);
        }
        catch (Exception error)
        {
            return new($"{error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}", null, null, null, "");
        }

        var old = Read(() =>
            repo.Head.Tip!.Parents.FirstOrDefault() is { } parent
                ? ((Blob)parent["src/a.cs"].Target).GetContentText()
                : "!no HEAD~1 (shallow)"
        );
        var options = new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
        };
        return new(
            null,
            () => repo.Head.Tip?.Sha ?? "",
            () => repo.Index.Select(e => e.Path).Concat(repo.RetrieveStatus(options).Untracked.Select(e => e.FilePath)),
            () =>
                repo.RetrieveStatus(options)
                    .Where(e => e.State is not FileStatus.Unaltered and not FileStatus.Ignored)
                    .Select(e => e.FilePath),
            old
        );
    }

    private static Answers CheckGitReader(string root)
    {
        GitReader.Primitive.PrimitiveRepository repo;
        try
        {
            repo = Gr.Open(root);
        }
        catch (Exception error)
        {
            return new($"{error.GetType().Name}: {error.Message.ReplaceLineEndings(" ")}", null, null, null, "");
        }

        var old = Read(() =>
            Gr.Commit(repo, Gr.Head(repo)) is { } tip && tip.Parents.Count > 0
                ? Gr.FileAt(repo, tip.Parents[0], "src/a.cs")
                : "!no HEAD~1 (shallow)"
        );
        return new(null, () => Gr.Head(repo).ToString(), () => Gr.FileList(repo), () => Gr.Dirty(repo), old);
    }
}
