using AwesomeAssertions;
using Harness.Core.Architecture;
using Xunit;

namespace Harness.Unit.Architecture;

/// <summary>NuGet SemVer 2.0 comparison, version ranges, and what a package's status means (TASK-0141).</summary>
public sealed class PackageVersionsTests
{
    private static NuGetVersion V(string text) => NuGetVersion.Parse(text)!;

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0", "1.0.0.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("9.0.3", "10.0.0")]
    public void VersionsCompareBySemVerRules(string lower, string higher)
    {
        V(lower).Should().BeLessThan(V(higher));
        V(higher).Should().BeGreaterThan(V(lower));
    }

    [Fact]
    public void MetadataIsIgnoredLabelsCompareCaseInsensitivelyAndMissingPartsAreZero()
    {
        V("1.0.0+build.5").Should().Be(V("1.0.0+other"));
        V("1.0.0-RC.1").Should().Be(V("1.0.0-rc.1"));
        V("1.0").Should().Be(V("1.0.0.0"));
        V("1.0.0+sha.abc").Text.Should().Be("1.0.0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.*")]
    [InlineData("$(Version)")]
    [InlineData("1.0.0.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("v1.0")]
    public void WhatIsNoVersionDoesNotParse(string text) => NuGetVersion.Parse(text).Should().BeNull();

    [Theory]
    [InlineData("8.0.1", false, "8.0.1")]
    [InlineData("[8.0.1]", false, "8.0.1")]
    [InlineData("8.0.1", true, null)]
    [InlineData("$(EfVersion)", false, null)]
    [InlineData("8.*", false, null)]
    [InlineData("[8.0,9.0)", false, null)]
    [InlineData("(,9.0]", false, null)]
    public void OnlyAKnownVersionIsInUse(string declared, bool unresolved, string? expected) =>
        (NuGetVersion.InUse(declared, unresolved)?.Text).Should().Be(expected);

    [Theory]
    [InlineData("[1.0, 2.0)", "1.0", true)]
    [InlineData("[1.0, 2.0)", "1.9.9", true)]
    [InlineData("[1.0, 2.0)", "2.0", false)]
    [InlineData("(, 5.8.4)", "5.8.3", true)]
    [InlineData("(, 5.8.4)", "5.8.4", false)]
    [InlineData("(1.0,)", "1.0", false)]
    [InlineData("(1.0,)", "1.0.1", true)]
    [InlineData("[1.0]", "1.0.0", true)]
    [InlineData("1.0", "3.0", true)]
    [InlineData("[2.0.0, 2.0.8)", "2.0.0-beta", false)]
    public void RangesUseIntervalNotation(string range, string version, bool contains) =>
        NuGetVersionRange.Parse(range)!.Contains(V(version)).Should().Be(contains);

    [Fact]
    public void ANewerStableVersionIgnoresPreReleases()
    {
        string[] available = ["8.0.0", "8.0.1", "9.0.0-preview.1", "9.0.3", "10.0.0-rc.2"];
        PackageCheck.NewerStable(V("8.0.1"), available)!.Text.Should().Be("9.0.3");
        PackageCheck.NewerStable(V("9.0.3"), available).Should().BeNull("only a pre-release is above it");
    }

    [Fact]
    public void APackageOnAPreReleaseIsComparedWithTheNextStableVersionAboveIt()
    {
        PackageCheck.NewerStable(V("2.0.0-beta.3"), ["1.9.0", "2.0.0-beta.4", "2.0.0"])!.Text.Should().Be("2.0.0");
        PackageCheck.NewerStable(V("2.0.0-beta.3"), ["1.9.0", "2.0.0-beta.4"]).Should().BeNull();
    }

    [Fact]
    public void AVulnerableVersionNamesItsAdvisoryAndTheFirstFixedVersion()
    {
        PackageAdvisory[] advisories =
        [
            new(1, "https://github.com/advisories/GHSA-low", "[2.0.0, 2.0.8)"),
            new(3, "https://github.com/advisories/GHSA-critical", "(, 2.1.0)"),
            new(2, "https://github.com/advisories/GHSA-other", "[3.0.0, 3.1.0)"),
        ];
        var found = PackageCheck.Vulnerable(V("2.0.5"), advisories, ["2.0.5", "2.0.8", "2.1.0-rc.1", "2.1.0", "3.0.0"]);

        found.Select(v => (v.Severity, v.Fixed)).Should().Equal(("critical", "2.1.0"), ("moderate", "2.0.8"));
        found[0].Link.Should().Be("https://github.com/advisories/GHSA-critical");
        PackageCheck
            .Vulnerable(V("2.0.5"), advisories, [])
            .Select(v => v.Fixed)
            .Should()
            .Equal(["2.1.0", "2.0.8"], "without a version list the range's exclusive upper bound is the fix");
        PackageCheck.Vulnerable(V("2.1.0"), advisories, []).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "warn")]
    [InlineData(1, "warn")]
    [InlineData(2, "problem")]
    [InlineData(3, "problem")]
    public void AVulnerablePackageIsAFindingWhoseLevelFollowsItsWorstSeverity(int severity, string level)
    {
        var unit = Unit(new BuildPackage("Acme.Json", "1.2.0"));
        var status = new PackageStatus(
            "Acme.Json",
            "1.2.0",
            null,
            PackageCheck.Vulnerable(V("1.2.0"), [new(severity, "https://example.org/advisory", "[1.0, 1.3)")], [])
        );

        var finding = PackageCheck.Findings(unit, Statuses(status), outdated: false).Single();

        finding.Kind.Should().Be("vulnerable-package");
        finding.Level.Should().Be(level);
        finding.Why.Should().Contain("https://example.org/advisory").And.Contain("[1.0, 1.3)");
        finding.Fix.Should().Contain("1.3");
        finding.Key.Should().Be("vulnerable-package:src/App/App.csproj|Acme.Json");
        finding.Source.Tool.Should().Be("internal/packages");
    }

    [Fact]
    public void OutdatedPackagesAreInfoFindingsOnlyWhenAskedFor()
    {
        var unit = Unit(new BuildPackage("Acme.Json", "1.2.0"));
        var statuses = Statuses(new PackageStatus("Acme.Json", "1.2.0", "2.0.0", []));

        PackageCheck.Findings(unit, statuses, outdated: false).Should().BeEmpty();
        var outdated = PackageCheck.Findings(unit, statuses, outdated: true).Single();
        outdated.Kind.Should().Be("outdated-package");
        outdated.Level.Should().Be("info");
        outdated.Title.Should().Contain("2.0.0");
    }

    [Fact]
    public void CentrallyManagedVersionsAreMatchedAndUnresolvedOnesGetNoFinding()
    {
        // StaticMsBuild gives a centrally managed package the version Directory.Packages.props declares
        var unit = Unit(
            new BuildPackage("Acme.Central", "3.1.0"),
            new BuildPackage("Acme.Property", "$(AcmeVersion)", Unresolved: true),
            new BuildPackage("Acme.Float", "3.*")
        );
        var vulnerable = PackageCheck.Vulnerable(V("3.1.0"), [new(2, "https://example.org/a", "(, 4.0)")], []);
        var statuses = Statuses(
            new PackageStatus("Acme.Central", "3.1.0", "4.0.0", vulnerable),
            new PackageStatus("Acme.Property", "$(AcmeVersion)", "4.0.0", vulnerable),
            new PackageStatus("Acme.Float", "3.*", "4.0.0", vulnerable)
        );

        PackageCheck
            .Findings(unit, statuses, outdated: true)
            .Select(f => f.Kind + " " + f.Title.Split(' ')[2])
            .Should()
            .Equal("vulnerable-package Acme.Central", "outdated-package Acme.Central");
    }

    private static BuildUnit Unit(params BuildPackage[] packages)
    {
        var unit = new BuildUnit { Name = "App", Path = "src/App/App.csproj" };
        unit.Packages.AddRange(packages);
        return unit;
    }

    private static Dictionary<string, PackageStatus> Statuses(params PackageStatus[] statuses) =>
        statuses.ToDictionary(status => PackageCheck.Key(status.Name, status.Version), StringComparer.Ordinal);
}
