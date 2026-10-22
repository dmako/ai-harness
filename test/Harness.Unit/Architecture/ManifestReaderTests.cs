using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Infrastructure.Architecture;
using Xunit;

namespace Harness.Unit.Architecture;

public sealed class ManifestReaderTests
{
    [Fact]
    public void DeclarationsArePublicNonTestDefinitionsAndRetainBaseEvidence()
    {
        using var tree = new ReaderTree(
            new()
            {
                ["src/A.cs"] = """
                namespace Example.Core;
                public sealed class AccountService<T> : Base<T>, Example.IAccount where T : class {}
                public readonly record struct Revision(int N);
                internal class Hidden {}
                """,
                ["src/a.ts"] = """
                export class PdfRenderer extends Example.Base implements IRender {}
                export type Invoice = {};
                """,
                ["test/A.cs"] = "public class Ignored {}",
                ["src/a.d.ts"] = "export interface Ignored {}",
                ["node_modules/a.ts"] = "export class Ignored {}",
            }
        );
        var declarations = DeclarationReader.Read(tree.Repository());
        declarations
            .Select(d => d.Name)
            .Should()
            .BeEquivalentTo("AccountService", "Revision", "PdfRenderer", "Invoice");
        declarations.Single(d => d.Name is "AccountService").Bases.Should().Equal("Base", "IAccount");
        declarations.Single(d => d.Name is "PdfRenderer").Bases.Should().Equal("Base", "IRender");
    }

    [Fact]
    public void ReadFailuresAreCachedAndXmlDoesNotExecuteExternalEntities()
    {
        using var tree = new ReaderTree(
            new()
            {
                ["App.csproj"] =
                    """<!DOCTYPE Project [<!ENTITY xxe SYSTEM "file:///does-not-exist">]><Project><PropertyGroup><Version>&xxe;</Version></PropertyGroup></Project>""",
            }
        );
        var repository = tree.Repository();
        new StaticMsBuild(repository).Evaluate("App.csproj").Packages.Should().BeEmpty();
        repository.Read("../escape.cs").Should().BeEmpty();
        repository
            .Notes.Should()
            .ContainSingle(note => note.Contains("leaves the repository", StringComparison.Ordinal));
        repository.Read("missing.cs").Should().BeEmpty();
        repository.Read("missing.cs").Should().BeEmpty();
        repository.Notes.Should().HaveCount(2);
        File.Exists(Path.Combine(tree.Root, "missing.cs")).Should().BeFalse();
    }

    [Fact]
    public void UnixAnalysisLinksCannotEscapeAndMalformedOrOversizedFilesRemainPartialEvidence()
    {
        if (OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip(
                "Unix symlink fixture; Windows creation may require developer mode or privileges."
            );
        }

        using var tree = new ReaderTree(new() { ["inside.cs"] = "public class Inside {}" });
        using var other = new ReaderTree(new() { ["outside.cs"] = "public class Outside {}" });
        File.CreateSymbolicLink(Path.Combine(tree.Root, "escape.cs"), Path.Combine(other.Root, "outside.cs"));
        File.CreateSymbolicLink(Path.Combine(tree.Root, "link.cs"), "inside.cs");
        File.WriteAllBytes(Path.Combine(tree.Root, "invalid.cs"), [0xff]);
        File.WriteAllBytes(Path.Combine(tree.Root, "oversized.cs"), new byte[4 * 1024 * 1024 + 1]);
        var repository = tree.Repository();
        repository.Read("link.cs").Should().Be("public class Inside {}");
        repository.Read("escape.cs").Should().BeEmpty();
        repository.Read("invalid.cs").Should().BeEmpty();
        repository.Read("oversized.cs").Should().BeEmpty();
        repository.Notes.Should().HaveCount(3);
    }
}

internal sealed class ReaderTree : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("harness-reader-").FullName;
    public IReadOnlyList<string> Files { get; }

    public ReaderTree(Dictionary<string, string> files)
    {
        Files = files.Keys.Order(StringComparer.Ordinal).ToArray();
        foreach (var pair in files)
        {
            var path = Path.Combine(Root, pair.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, pair.Value);
        }
    }

    public AnalysisRepository Repository() => new(Root, Files, TestContext.Current.CancellationToken);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
