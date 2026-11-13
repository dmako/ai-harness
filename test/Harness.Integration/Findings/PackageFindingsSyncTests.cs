using AwesomeAssertions;
using Harness.Infrastructure.Architecture.PackageFeeds;
using Harness.Infrastructure.Findings;
using Harness.Integration.Sync;
using Xunit;

namespace Harness.Integration.Findings;

/// <summary>
/// A sync turns the cached package metadata into vulnerable-package findings, and with [architecture.packages]
/// outdated into outdated-package notes, without reading a package source (TASK-0141).
/// </summary>
public sealed class PackageFindingsSyncTests
{
    private const string Feed = "https://feed.test/v3/index.json";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASyncFindsVulnerablePackagesFromTheCacheAndOutdatedOnesWhenAskedFor()
    {
        using var fixture = await SyncFixture.CreateAsync();
        fixture.Write(
            "nuget.config",
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{Feed}" />
              </packageSources>
              <packageSourceMapping>
                <clear />
                <packageSource key="feed"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """
        );
        fixture.Write(
            "Directory.Packages.props",
            """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="Acme.Json" Version="8.0.1" /></ItemGroup>
            </Project>
            """
        );
        fixture.Write(
            "App/App.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Acme.Json" />
                <PackageReference Include="Acme.Http" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """
        );
        await fixture.CommitAsync("an app with packages");

        var cache = PackageMetadataCache.For(Path.Combine(fixture.Global, "config.toml"));
        var at = DateTimeOffset.UtcNow.AddHours(-2);
        cache.SaveService(
            Feed,
            new(at, "https://feed.test/v3-flatcontainer/", "https://feed.test/v3/vulnerabilities/index.json")
        );
        cache.SaveVersions(Feed, "Acme.Json", new(at, true, ["8.0.1", "9.0.0"]));
        cache.SaveVersions(Feed, "Acme.Http", new(at, true, ["1.0.0", "1.1.0"]));
        cache.SaveVulnerabilities(Feed, new(at, [new("base", "https://feed.test/base.json", "1")]));
        cache.SavePage(
            Feed,
            "base",
            """
            {"acme.json":[{"url":"https://github.com/advisories/GHSA-acme-json","severity":2,"versions":"[8.0.0, 9.0.0)"}],
             "acme.http":[{"url":"https://github.com/advisories/GHSA-acme-http","severity":1,"versions":"(, 1.1.0)"}]}
            """
        );

        string[] Packages()
        {
            using var db = fixture.Database();
            return new FindingStore(db)
                .Current(Token)
                .Findings.Where(f => f.Status is not "gone" && f.Kind.EndsWith("-package", StringComparison.Ordinal))
                .Select(f => f.Kind + " " + f.Level + " " + f.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }

        await fixture.RunAsync();
        Packages()
            .Should()
            .Equal(
                "vulnerable-package problem vulnerable-package:App/App.csproj|Acme.Json",
                "vulnerable-package warn vulnerable-package:App/App.csproj|Acme.Http"
            );

        await File.AppendAllTextAsync(
            Path.Combine(fixture.Global, "config.toml"),
            "\n[architecture.packages]\noutdated = true\n",
            Token
        );
        await fixture.RunAsync();
        Packages()
            .Should()
            .Contain([
                "outdated-package info outdated-package:App/App.csproj|Acme.Json",
                "outdated-package info outdated-package:App/App.csproj|Acme.Http",
            ]);
    }
}
