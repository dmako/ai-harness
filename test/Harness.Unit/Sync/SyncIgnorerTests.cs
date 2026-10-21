using AwesomeAssertions;
using Harness.Core.Sync;
using Xunit;

namespace Harness.Unit.Sync;

public sealed class SyncIgnorerTests
{
    [Theory]
    [InlineData(".harness/harness.db", true)]
    [InlineData(".harness", true)]
    [InlineData("a/vendor/x.cs", true)]
    [InlineData("web/app.min.js", true)]
    [InlineData("doc/generated/api.md", true)]
    [InlineData("doc/generated/sub/api.md", false)]
    [InlineData("node_modules/pkg/index.js", true)]
    [InlineData("src/main.cs", false)]
    [InlineData("vendored.cs", false)]
    [InlineData("a/test1.cs", true)]
    [InlineData("a/testx.cs", false)]
    public void MatchesPinnedDirectoryBasenameAndPathGlobRules(string path, bool ignored) =>
        new SyncIgnorer(["vendor/", "*.min.js", "doc/generated/*.md", "/node_modules/", "test[0-9].cs"]).Ignored(path).Should().Be(ignored);

    [Fact]
    public void RenamesCrossingIgnoreBoundaryBecomeAdditionsOrDeletions()
    {
        new SyncIgnorer(["tmp/"]).Filter([new("modified", "a.cs"), new("added", "tmp/x"), new("renamed", "tmp/b.cs", "b.cs"), new("renamed", "c.cs", "tmp/c.cs"), new("renamed", "e.cs", "d.cs")])
            .Should().Equal(new GitChange("modified", "a.cs"), new GitChange("deleted", "b.cs"), new GitChange("added", "c.cs"), new GitChange("renamed", "e.cs", "d.cs"));
    }
}
