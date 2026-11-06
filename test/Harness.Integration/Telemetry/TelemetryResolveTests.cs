using AwesomeAssertions;
using Harness.Core.Telemetry;
using Harness.Infrastructure.Git;
using Harness.Infrastructure.Telemetry;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Telemetry;

public sealed class TelemetryResolveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Method(string type, string name, string body, string calls = "") =>
        $"class {type}\n{{\n    public void {name}()\n    {{\n        {calls}var x = {body};\n    }}\n}}\n";

    [Fact]
    public async Task ResolvingCallsFromSharedHeadsGivesTheSameOutcomesAndCallers()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        var repository = location.Repository!;
        fixture.Write("a.cs", Method("A", "Run", "0", "Helper(); Spaced(); Uni(); "));
        fixture.Write("b.cs", Method("B", "Helper", "1"));
        fixture.Write("c.cs", Method("C", "Other", "0", "Gone(); "));
        fixture.Write("gone.cs", Method("G", "Gone", "1"));
        fixture.Write("my file.cs", Method("S", "Spaced", "1"));
        fixture.Write("ünï.cs", Method("U", "Uni", "1"));
        fixture.Write("old.cs", Method("O", "Old", "1"));
        await fixture.CommitAsync("first");
        var first = (await repository.ReadHeadAsync(Token)).Commit;
        using var db = fixture.Database();
        var store = new TelemetryStore(db);
        void Record(string head, string task, params string[] paths) =>
            store.RecordContext(
                new(
                    fixture.Worktree,
                    head,
                    task,
                    1,
                    1,
                    [.. paths.Select(p => new ContextUnitObservation(p, "X", "definition", "fixture", "code"))]
                ),
                Token
            );
        Record(first, "a1", "a.cs");
        Record(first, "a2", "a.cs");
        Record(first, "c1", "c.cs");
        Record(first, "multi", "c.cs", "a.cs");

        fixture.Write("b.cs", Method("B", "Helper", "2"));
        fixture.Write("gone.cs", Method("G", "Gone", "2"));
        fixture.Write("my file.cs", Method("S", "Spaced", "2"));
        fixture.Write("ünï.cs", Method("U", "Uni", "2"));
        File.Delete(Path.Combine(fixture.Root, "old.cs"));
        fixture.Write("new.cs", Method("N", "Fresh", "1"));
        await fixture.CommitAsync("second");
        var second = (await repository.ReadHeadAsync(Token)).Commit;
        Record(second, "b2", "b.cs");

        fixture.Write("a.cs", Method("A", "Run", "1", "Helper(); Spaced(); Uni(); "));
        await fixture.CommitAsync("third");
        var head = (await repository.ReadHeadAsync(Token)).Commit;

        var starts = new List<string>();
        void Count(string root, IReadOnlyList<string> arguments)
        {
            if (GitRepository.PathEquals(root, fixture.Root))
            {
                lock (starts)
                {
                    starts.Add(string.Join(' ', arguments));
                }
            }
        }

        GitRepository.Started += Count;
        ContextResolution resolved;
        try
        {
            resolved = await store.ResolveAsync(repository, fixture.Worktree, head, "sync", Token);
        }
        finally
        {
            GitRepository.Started -= Count;
        }

        resolved.Calls.Should().Be(5);
        resolved.Callees.Should().Be(11);
        // recorded from the resolve that started one git process per call and file (before TASK-0100)
        string[] recorded =
        [
            "a1 a.cs hit=1 from=-",
            "a1 b.cs hit=0 from=a.cs",
            "a1 gone.cs hit=0 from=-",
            "a1 my file.cs hit=0 from=a.cs",
            "a1 new.cs hit=0 from=-",
            "a1 old.cs hit=0 from=-",
            "a1 ünï.cs hit=0 from=a.cs",
            "a2 a.cs hit=1 from=-",
            "a2 b.cs hit=0 from=a.cs",
            "a2 gone.cs hit=0 from=-",
            "a2 my file.cs hit=0 from=a.cs",
            "a2 new.cs hit=0 from=-",
            "a2 old.cs hit=0 from=-",
            "a2 ünï.cs hit=0 from=a.cs",
            "b2 a.cs hit=0 from=-",
            "c1 a.cs hit=0 from=-",
            "c1 b.cs hit=0 from=-",
            "c1 gone.cs hit=0 from=c.cs",
            "c1 my file.cs hit=0 from=-",
            "c1 new.cs hit=0 from=-",
            "c1 old.cs hit=0 from=-",
            "c1 ünï.cs hit=0 from=-",
            "multi a.cs hit=1 from=-",
            "multi b.cs hit=0 from=a.cs",
            "multi gone.cs hit=0 from=c.cs",
            "multi my file.cs hit=0 from=a.cs",
            "multi new.cs hit=0 from=-",
            "multi old.cs hit=0 from=-",
            "multi ünï.cs hit=0 from=a.cs",
        ];
        Outcomes(fixture).Should().Equal(recorded);
        // two distinct (from, to) pairs: one name list and one hunk diff each, and one blob reader for the resolve
        starts.Should().HaveCount(5, string.Join("; ", starts));
        starts.Count(s => s.Contains("--name-only", StringComparison.Ordinal)).Should().Be(2);
        starts.Count(s => s.Contains("-U0", StringComparison.Ordinal)).Should().Be(2);
        starts.Count(s => s.Contains("cat-file --batch", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void ADiffOverManyFilesIsSplitByEachFilesHeaderWhateverItsNameOrContent()
    {
        const string Diff = """
            diff --git a/my file.cs b/my file.cs
            index 1..2 100644
            --- a/my file.cs<TAB>
            +++ b/my file.cs<TAB>
            @@ -5 +5 @@ public void Spaced()
            --- not a header: a removed line starting with two dashes
            +++ not a header either
            @@ -9 +9 @@ public void Later()
            diff --git "a/t\tab.cs" "b/t\tab.cs"
            --- "a/t\tab.cs"
            +++ "b/t\tab.cs"
            @@ -1 +1 @@ void Tab()
            diff --git a/\303\274.cs b/\303\274.cs
            --- "a/\303\274.cs"
            +++ "b/\303\274.cs"
            @@ -1 +1 @@ void U()
            diff --git a/gone.cs b/gone.cs
            deleted file mode 100644
            --- a/gone.cs
            +++ /dev/null
            @@ -1,3 +0,0 @@
            diff --git a/image.png b/image.png
            Binary files a/image.png and b/image.png differ
            """;
        // git ends a name holding whitespace with a tab
        var files = GitRepository.HunksByFile(
            Diff.ReplaceLineEndings("\n").Replace("<TAB>", "\t", StringComparison.Ordinal)
        );
        files.Keys.Should().BeEquivalentTo("my file.cs", "t\tab.cs", "ü.cs", "gone.cs");
        files["my file.cs"].Should().Equal("@@ -5 +5 @@ public void Spaced()", "@@ -9 +9 @@ public void Later()");
        files["t\tab.cs"].Should().ContainSingle();
        files["ü.cs"].Should().ContainSingle();
        files["gone.cs"].Should().Equal("@@ -1,3 +0,0 @@");
    }

    [Fact]
    public async Task OneBlobReaderReadsFilesAtACommitAndTellsWhatIsNotAFile()
    {
        using var fixture = await SyncFixture.CreateAsync();
        var location = await fixture.Lifecycle.LocateAsync(fixture.Root, cancellationToken: Token);
        var repository = location.Repository!;
        fixture.Write("dir/a.cs", "class A {}\n");
        fixture.Write("ü b.cs", "class Ü {}\n");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "bin.dat"), [0xff, 0xfe, 0x00, 0x80], Token);
        await fixture.CommitAsync("files");
        var head = (await repository.ReadHeadAsync(Token)).Commit;

        var starts = 0;
        void Count(string root, IReadOnlyList<string> arguments)
        {
            if (GitRepository.PathEquals(root, fixture.Root))
            {
                Interlocked.Increment(ref starts);
            }
        }

        GitRepository.Started += Count;
        try
        {
            await using var blobs = repository.OpenBlobReader();
            (await blobs.ReadAsync(head, "dir/a.cs", Token)).Should().Be("class A {}\n");
            (await blobs.ReadAsync(head, "ü b.cs", Token))
                .Should()
                .Be(await repository.FileAtAsync(head, "ü b.cs", Token));
            (await blobs.ReadAsync(head, "missing.cs", Token)).Should().BeNull();
            (await blobs.ReadAsync(head, "dir", Token)).Should().BeNull("a directory is no file");
            (await blobs.ReadAsync(head, "bin.dat", Token)).Should().BeNull("the file is not UTF-8 text");
            (await blobs.ReadAsync(head, "dir/a.cs", Token)).Should().Be("class A {}\n", "the reader keeps reading");
            await FluentActions
                .Awaiting(() => blobs.ReadAsync(head, "line\nbreak", Token))
                .Should()
                .ThrowAsync<IOException>();
        }
        finally
        {
            GitRepository.Started -= Count;
        }

        starts.Should().Be(2, "one cat-file --batch for every read, and the git show compared with");
    }

    private static string[] Outcomes(SyncFixture fixture)
    {
        using var db = fixture.Database();
        using var connection = db.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.task, o.path, o.hit, coalesce(o.called_from, '-') FROM context_outcomes o "
            + "JOIN context_calls c ON c.id = o.call_id ORDER BY c.task, o.path";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(
                $"{reader.GetString(0)} {reader.GetString(1)} hit={reader.GetInt64(2)} from={reader.GetString(3)}"
            );
        }

        return [.. rows];
    }
}
