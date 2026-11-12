using AwesomeAssertions;
using Harness.Scm;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class UnifiedDiffTests
{
    private const string Hunks = """
        @@ -1,3 +1,4 @@
         one
        -two
        +2
        +2b
         three
        @@ -10,2 +11,2 @@ class Parser
         ten
        -eleven
        +11
        \ No newline at end of file
        """;

    [Theory]
    [InlineData(1, DiffLineType.New, 1, 1)]
    [InlineData(2, DiffLineType.New, null, 2)]
    [InlineData(3, DiffLineType.New, null, 3)]
    [InlineData(4, DiffLineType.New, 3, 4)]
    [InlineData(2, DiffLineType.Old, 2, null)]
    [InlineData(3, DiffLineType.Old, 3, 4)]
    [InlineData(11, DiffLineType.New, 10, 11)]
    [InlineData(12, DiffLineType.New, null, 12)]
    [InlineData(11, DiffLineType.Old, 11, null)]
    public void LinesAreLocatedOnTheirSideWithTheOtherSidesNumberForContext(
        int line,
        DiffLineType type,
        int? oldLine,
        int? newLine
    ) => UnifiedDiff.Locate(Hunks, line, type).Should().Be(new DiffLine(oldLine, newLine));

    [Theory]
    [InlineData(5, DiffLineType.New)]
    [InlineData(4, DiffLineType.Old)]
    [InlineData(1, DiffLineType.New, "")]
    public void LinesOutsideTheHunksAreNotFound(int line, DiffLineType type, string? diff = null) =>
        UnifiedDiff.Locate(diff ?? Hunks, line, type).Should().BeNull();

    [Fact]
    public void WholePatchesSplitIntoFilesWithRenamesAdditionsDeletionsAndBinaries()
    {
        const string Patch = """
            diff --git a/src/a.cs b/src/a.cs
            index 1..2 100644
            --- a/src/a.cs
            +++ b/src/a.cs
            @@ -1 +1 @@
            -a
            +b
            diff --git a/old name.txt b/new name.txt
            similarity index 90%
            rename from old name.txt
            rename to new name.txt
            diff --git a/added.txt b/added.txt
            new file mode 100644
            --- /dev/null
            +++ b/added.txt
            @@ -0,0 +1 @@
            +x
            diff --git a/gone.txt b/gone.txt
            deleted file mode 100644
            diff --git a/logo.png b/logo.png
            Binary files a/logo.png and b/logo.png differ
            """;

        var files = UnifiedDiff.SplitFiles(Patch.Replace("\r\n", "\n", StringComparison.Ordinal));

        files
            .Select(file => file.NewPath)
            .Should()
            .Equal("src/a.cs", "new name.txt", "added.txt", "gone.txt", "logo.png");
        files[0].Diff.Should().Be("@@ -1 +1 @@\n-a\n+b\n");
        files[1].Should().Match<ScmFileDiff>(file => file.RenamedFile && file.OldPath == "old name.txt");
        files[2].NewFile.Should().BeTrue();
        files[2].Diff.Should().Be("@@ -0,0 +1 @@\n+x\n");
        files[3].DeletedFile.Should().BeTrue();
        files[4].Binary.Should().BeTrue();
    }

    [Fact]
    public void BudgetsKeepEveryFileListedAndCutAtLineEnds()
    {
        ScmFileDiff File(string path, string diff, bool truncated = false) =>
            new(path, path, false, false, false, false, diff, truncated);

        var whole = ScmDiffBudget.Apply([File("a", "12345\n"), File("b", "abc\n")], 100);
        whole.Truncated.Should().BeFalse();
        whole.OmittedFiles.Should().Be(0);

        var cut = ScmDiffBudget.Apply([File("a", "12345\n"), File("b", "ab\ncd\nef\n"), File("c", "zz\n")], 10);
        cut.Truncated.Should().BeTrue();
        cut.Files.Select(file => (file.NewPath, file.Diff, file.Truncated))
            .Should()
            .Equal(("a", "12345\n", false), ("b", "ab\n", true), ("c", "", true));
        cut.OmittedFiles.Should().Be(1);

        ScmDiffBudget.Apply([File("big", "x", truncated: true)], 10).Truncated.Should().BeTrue();
    }
}
