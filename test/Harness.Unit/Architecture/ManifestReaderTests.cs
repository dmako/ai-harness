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
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("evaluate", "dotnet")]
    [InlineData("ecosystem", "dotnet")]
    [InlineData("stack", "dotnet")]
    [InlineData("build", "dotnet")]
    [InlineData("ecosystem", "node")]
    [InlineData("stack", "node")]
    [InlineData("build", "node")]
    [InlineData("build", "monorepo")]
    [InlineData("ecosystem", "go")]
    [InlineData("stack", "go")]
    [InlineData("build", "go")]
    [InlineData("stack", "dart")]
    public async Task ReadersMatchTheFrozenGoImplementation(string mode, string language)
    {
        var oracle = Oracle();
        using var tree = new ReaderTree(Files(language));
        var repository = tree.Repository();
        var ecosystem = new EcosystemReader(repository);
        object? answer = mode switch
        {
            "evaluate" => Evaluated(ecosystem),
            "stack" => new StackReader(repository, ecosystem).Read(),
            "build" => new BuildReader(repository, ecosystem).Read(),
            _ => repository
                .Files.Where(file => EcosystemReader.Of(file).Length > 0)
                .Select(file =>
                {
                    var package = ecosystem.Package(file);
                    return new
                    {
                        path = file,
                        ecosystem = EcosystemReader.Of(file),
                        name = package.Name,
                        published = package.Published && package.Name.Length > 0,
                        dependencies = ecosystem.Dependencies(file),
                    };
                })
                .ToArray(),
        };
        var expected = await RunAsync(
            oracle,
            new
            {
                mode,
                root = tree.Root,
                files = repository.Files,
                project = "src/App/App.csproj",
            }
        );
        var actual = JsonSerializer.SerializeToNode(answer);
        JsonNode.DeepEquals(actual, expected).Should().BeTrue($"C#: {actual}\nGo: {expected}");
        if (mode is "evaluate")
        {
            var reads = repository.PhysicalReads;
            ecosystem.Build.Evaluate("src/App/App.csproj");
            repository.PhysicalReads.Should().Be(reads);
        }
    }

    private static object Evaluated(EcosystemReader ecosystem)
    {
        var evaluated = ecosystem.Build.Evaluate("src/App/App.csproj");
        return new
        {
            frameworks = evaluated.Frameworks,
            properties = evaluated.Properties,
            locations = evaluated.PropertyLocations,
            packages = evaluated.Packages.Select(p => new
            {
                p.Name,
                p.Version,
                p.At,
                p.Frameworks,
                p.Conditional,
                p.Unresolved,
            }),
            references = evaluated.References.Select(r => new
            {
                r.Path,
                r.At,
                r.Frameworks,
                r.Conditional,
            }),
        };
    }

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
                ["lib/a.go"] = "package lib\ntype Account struct {}\nfunc Generate[T any]() {}\n",
                ["test/A.cs"] = "public class Ignored {}",
                ["src/a.d.ts"] = "export interface Ignored {}",
                ["node_modules/a.ts"] = "export class Ignored {}",
            }
        );
        var declarations = DeclarationReader.Read(tree.Repository());
        declarations
            .Select(d => d.Name)
            .Should()
            .BeEquivalentTo("AccountService", "Revision", "PdfRenderer", "Invoice", "Account", "Generate");
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

    internal static Dictionary<string, string> Files(string language) =>
        language switch
        {
            "dotnet" => new()
            {
                ["Directory.Build.props"] = """
                    <Project>
                      <Import Project="build/versions.props" />
                      <PropertyGroup><PackageVersion>1.2.3</PackageVersion></PropertyGroup>
                    </Project>
                    """,
                ["build/versions.props"] =
                    "<Project><PropertyGroup><EfVersion>10.0.8</EfVersion><CommonVersion>4.1.0</CommonVersion></PropertyGroup></Project>",
                ["Directory.Packages.props"] = """
                    <Project><ItemGroup>
                      <PackageVersion Include="Common" Version="$(CommonVersion)" />
                    </ItemGroup></Project>
                    """,
                ["src/Directory.Build.props"] = """
                    <Project><Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" /></Project>
                    """,
                ["Directory.Build.targets"] = """
                    <Project><ItemGroup><PackageReference Update="Common" Version="9.0" /><PackageReference Remove="Unknown" Condition="Exists('absent.file')" /></ItemGroup></Project>
                    """,
                ["src/App/App.csproj"] = """
                    <Project Sdk="Microsoft.NET.Sdk.Web">
                      <PropertyGroup><TargetFrameworks>net10.0;netstandard2.0;net48</TargetFrameworks></PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Common" />
                        <PackageReference Include="Microsoft.EntityFrameworkCore" Version="$(EfVersion)" />
                        <PackageReference Include="Missing" Version="$(Nowhere)" />
                        <PackageReference Include="Unknown" Version="1" Condition="$([MSBuild]::IsOSPlatform('Windows'))" />
                        <PackageReference Include="Versioned" Version="2" Condition="'$(TargetFramework)' == 'net10.0'" />
                        <PackageReference Include="Versioned" Version="1" Condition="'$(TargetFramework)' != 'net10.0'" />
                        <ProjectReference Include="../Lib/Lib.csproj" Condition="'$(TargetFramework)' == 'net10.0'" />
                      </ItemGroup>
                      <ItemGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETFramework'"><PackageReference Include="Legacy" Version="3" /></ItemGroup>
                    </Project>
                    """,
                ["src/App/Program.cs"] = "public class Program {}",
                ["src/Lib/Lib.csproj"] =
                    "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>Example.Core</PackageId></PropertyGroup></Project>",
                ["App.slnx"] =
                    """<Solution><Folder Name="/src/"><Project Path="src/App/App.csproj" /><Project Path="src/Lib/Lib.csproj" /><Project Path="src/Missing.csproj" /></Folder></Solution>""",
            },
            "node" => new()
            {
                ["package.json"] = """
                    {"name":"app","engines":{"node":">=22"},"dependencies":{"react":"18.3.1","pdfkit":"^0.15.0"},"devDependencies":{"typescript":"5.5.4","vite":"6.2.2"},"optionalDependencies":{"local":"file:../local"}}
                    """,
                ["tsconfig.json"] = """{"compilerOptions":{"baseUrl":".","paths":{"@/*":["src/*"]}}}""",
                ["src/main.tsx"] = "import { format } from '@/shared/format';\nimport React from 'react';\n",
                ["src/shared/format.ts"] = "export function format() {}",
                ["src/features/pdf/index.ts"] = "export { render } from './render';",
                ["src/features/pdf/render.ts"] = "import pdf from 'pdfkit';\nexport function render() {}",
                ["src/features/pdf/render.test.ts"] = "import ignored from 'not-a-production-dependency';",
                [".nvmrc"] = "18.0.0",
            },
            "monorepo" => new()
            {
                ["package.json"] =
                    """{"name":"root","private":true,"workspaces":{"packages":["packages/*","apps/*"]}}""",
                ["packages/core/package.json"] =
                    """{"name":"@acme/core","version":"3.2.0","main":"index.ts","dependencies":{"lodash":"4"}}""",
                ["packages/core/index.ts"] = "export function format() {}",
                ["apps/web/package.json"] =
                    """{"name":"web","private":true,"dependencies":{"@acme/core":"workspace:*","react":"18"}}""",
                ["apps/web/src/index.tsx"] = "import { format } from '@acme/core';\nimport React from 'react';",
            },
            "go" => new()
            {
                ["go.mod"] = "module example.com/app\n\ngo 1.27.1\nrequire github.com/spf13/cobra v1.9.0\n",
                ["cmd/app/main.go"] =
                    "package main\nimport (\n \"example.com/app/internal/core\"\n \"github.com/spf13/cobra\"\n)\nfunc main() {}",
                ["internal/core/core.go"] = "package core\nfunc Run() {}",
                ["nested/go.mod"] = "module example.com/other\ngo 1.20\n",
                ["nested/other.go"] = "package other\nimport \"not-part-of-root\"",
            },
            _ => new()
            {
                ["lib/main.dart"] = "void main() {}",
                ["pubspec.yaml"] =
                    "name: app\nenvironment:\n  sdk: \">=3.4.0 <4.0.0\"\n  flutter: \">=3.22.0\"\ndependencies:\n  flutter:\n    sdk: flutter\n",
            },
        };

    private static string Oracle() =>
        Environment.GetEnvironmentVariable("HARNESS_GO_ECOSYSTEM_ORACLE") is { Length: > 0 } path
            ? path
            : throw Xunit.Sdk.SkipException.ForSkip("Set HARNESS_GO_ECOSYSTEM_ORACLE using Ecosystem/build_oracle.py.");

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "The explicitly configured frozen test oracle runs directly without a shell."
    )]
    private static async Task<JsonNode?> RunAsync(string oracle, object input)
    {
        using var process = new Process
        {
            StartInfo = new(oracle)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(Token);
        var stderr = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input));
            process.StandardInput.Close();
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            process.ExitCode.Should().Be(0, await stderr);
            return JsonNode.Parse(await stdout);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
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
