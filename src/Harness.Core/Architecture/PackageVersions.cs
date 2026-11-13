using System.Globalization;

namespace Harness.Core.Architecture;

/// <summary>
/// A NuGet package version as NuGet compares it (SemVer 2.0 with NuGet's legacy fourth part): up to four numbers,
/// optional pre-release labels after '-', build metadata after '+' ignored; labels compare case-insensitively,
/// numeric labels numerically and below alphanumeric ones, and a release ranks above its pre-releases.
/// </summary>
public sealed class NuGetVersion : IComparable<NuGetVersion>, IEquatable<NuGetVersion>
{
    private readonly long[] _numbers;
    private readonly string[] _labels;

    private NuGetVersion(long[] numbers, string[] labels, string text)
    {
        _numbers = numbers;
        _labels = labels;
        Text = text;
    }

    /// <summary>The version as written, without build metadata.</summary>
    public string Text { get; }

    public IReadOnlyList<long> Numbers => _numbers;

    public IReadOnlyList<string> Labels => _labels;

    public bool IsPrerelease => _labels.Length > 0;

    /// <summary>The version, or null when the text is no single version (a range, a float, a property).</summary>
    public static NuGetVersion? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var release = dash >= 0 ? value[..dash] : value;
        var parts = release.Split('.');
        if (release.Length is 0 || parts.Length > 4)
        {
            return null;
        }

        var numbers = new long[4];
        for (var index = 0; index < parts.Length; index++)
        {
            if (
                parts[index].Length is 0
                || !parts[index].All(char.IsAsciiDigit)
                || !long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])
            )
            {
                return null;
            }
        }

        string[] labels = [];
        if (dash >= 0)
        {
            labels = value[(dash + 1)..].Split('.');
            if (labels.Any(label => label.Length is 0 || !label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-')))
            {
                return null;
            }
        }

        return new(numbers, labels, value);
    }

    public int CompareTo(NuGetVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var index = 0; index < 4; index++)
        {
            var compared = _numbers[index].CompareTo(other._numbers[index]);
            if (compared is not 0)
            {
                return compared;
            }
        }

        return (_labels.Length, other._labels.Length) switch
        {
            (0, 0) => 0,
            (0, _) => 1,
            (_, 0) => -1,
            _ => CompareLabels(_labels, other._labels),
        };
    }

    private static int CompareLabels(string[] left, string[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumber = long.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            var rightNumber = long.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out var b);
            var compared = (leftNumber, rightNumber) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase),
            };
            if (compared is not 0)
            {
                return compared;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public bool Equals(NuGetVersion? other) => other is not null && CompareTo(other) is 0;

    public override bool Equals(object? obj) => obj is NuGetVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var number in _numbers)
        {
            hash.Add(number);
        }

        foreach (var label in _labels)
        {
            hash.Add(label, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Text;

    public static bool operator <(NuGetVersion? left, NuGetVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(NuGetVersion? left, NuGetVersion? right) => right < left;

    public static bool operator <=(NuGetVersion? left, NuGetVersion? right) => !(left > right);

    public static bool operator >=(NuGetVersion? left, NuGetVersion? right) => !(left < right);

    public static bool operator ==(NuGetVersion? left, NuGetVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(NuGetVersion? left, NuGetVersion? right) => !(left == right);

    /// <summary>
    /// The version a project declares it uses: a plain version (NuGet's 'at least', which restores that version
    /// when it exists) or an exact range [x]; null for what is not one known version: an unresolved $(...)
    /// property, a floating version (1.*) or an open range.
    /// </summary>
    public static NuGetVersion? InUse(string? declared, bool unresolved = false)
    {
        var text = (declared ?? "").Trim();
        if (
            unresolved
            || text.Length is 0
            || text.Contains("$(", StringComparison.Ordinal)
            || text.Contains('*', StringComparison.Ordinal)
        )
        {
            return null;
        }

        if (text[0] is '[' or '(')
        {
            return
                NuGetVersionRange.Parse(text) is { Min: { } min, Max: { } max, MinInclusive: true, MaxInclusive: true }
                && min == max
                ? min
                : null;
        }

        return Parse(text);
    }
}

/// <summary>
/// A NuGet version range in interval notation: "1.0" (1.0 or above), "[1.0]" (exactly), "[1.0,2.0)",
/// "(,1.0]", "(1.0,)"; an empty bound is open.
/// </summary>
public sealed record NuGetVersionRange(
    NuGetVersion? Min,
    bool MinInclusive,
    NuGetVersion? Max,
    bool MaxInclusive,
    string Text
)
{
    public static NuGetVersionRange? Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length is 0 || value.Contains('*', StringComparison.Ordinal))
        {
            return null;
        }

        if (value[0] is not ('[' or '('))
        {
            return NuGetVersion.Parse(value) is { } floor ? new(floor, true, null, false, value) : null;
        }

        if (value.Length < 3 || value[^1] is not (']' or ')'))
        {
            return null;
        }

        var minInclusive = value[0] is '[';
        var maxInclusive = value[^1] is ']';
        var inner = value[1..^1].Split(',');
        if (inner.Length is 1)
        {
            // [1.0] is exactly 1.0; (1.0) means nothing
            return minInclusive && maxInclusive && NuGetVersion.Parse(inner[0]) is { } exact
                ? new(exact, true, exact, true, value)
                : null;
        }

        if (inner.Length is not 2)
        {
            return null;
        }

        var min = inner[0].Trim().Length is 0 ? null : NuGetVersion.Parse(inner[0]);
        var max = inner[1].Trim().Length is 0 ? null : NuGetVersion.Parse(inner[1]);
        if (
            min is null && inner[0].Trim().Length > 0
            || max is null && inner[1].Trim().Length > 0
            || min is null && max is null
        )
        {
            return null;
        }

        return new(min, minInclusive, max, maxInclusive, value);
    }

    public bool Contains(NuGetVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var aboveMin = Min is null || (MinInclusive ? version >= Min : version > Min);
        var belowMax = Max is null || (MaxInclusive ? version <= Max : version < Max);
        return aboveMin && belowMax;
    }
}

/// <summary>One advisory of NuGet's VulnerabilityInfo resource: severity 0-3, its URL and the affected range.</summary>
public sealed record PackageAdvisory(int Severity, string Link, string Versions)
{
    public string SeverityName =>
        Severity switch
        {
            0 => "low",
            1 => "moderate",
            2 => "high",
            3 => "critical",
            _ => "unknown",
        };
}

/// <summary>An advisory the version in use falls into, with the first version outside its range when known.</summary>
public sealed record PackageVulnerability(string Severity, string Link, string Versions, string? Fixed);

/// <summary>What the package sources say of a package at the version a project uses.</summary>
public sealed record PackageStatus(
    string Name,
    string Version,
    string? Latest,
    IReadOnlyList<PackageVulnerability> Vulnerabilities
);

/// <summary>Compares the version in use with a package's versions and advisories.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "NuGet's V3 protocol names packages and versions in lower case."
)]
public static class PackageCheck
{
    /// <summary>
    /// The highest stable version above the one in use, or null: pre-releases are never offered, and a package on a
    /// pre-release is compared with the stable versions above it (2.0.0 is above 2.0.0-beta).
    /// </summary>
    public static NuGetVersion? NewerStable(NuGetVersion current, IEnumerable<string> available)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(available);
        return available
            .Select(NuGetVersion.Parse)
            .Where(version => version is { IsPrerelease: false } && version > current)
            .Max();
    }

    /// <summary>
    /// The advisories whose range holds the version in use. Fixed is the lowest listed version above it outside the
    /// range (stable unless the one in use is a pre-release), else the range's exclusive upper bound.
    /// </summary>
    public static IReadOnlyList<PackageVulnerability> Vulnerable(
        NuGetVersion current,
        IEnumerable<PackageAdvisory> advisories,
        IEnumerable<string> available
    )
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(advisories);
        var versions = available
            .Select(NuGetVersion.Parse)
            .OfType<NuGetVersion>()
            .Where(version => version > current && (current.IsPrerelease || !version.IsPrerelease))
            .Order()
            .ToArray();
        List<PackageVulnerability> found = [];
        foreach (var advisory in advisories)
        {
            if (NuGetVersionRange.Parse(advisory.Versions) is not { } range || !range.Contains(current))
            {
                continue;
            }

            var fixedIn =
                versions.FirstOrDefault(version => !range.Contains(version))?.Text
                ?? (range is { Max: { } max, MaxInclusive: false } ? max.Text : null);
            found.Add(new(advisory.SeverityName, advisory.Link, advisory.Versions, fixedIn));
        }

        return found
            .GroupBy(vulnerability => vulnerability.Link, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(vulnerability => Rank(vulnerability.Severity))
            .ToArray();
    }

    /// <summary>The key of a package at a version in a lookup's answer: both lower-case, joined by '@'.</summary>
    public static string Key(string name, string version) => (name + "@" + version).ToLowerInvariant();

    /// <summary>A severity's rank, 0 (low) to 3 (critical).</summary>
    public static int Rank(string severity) =>
        severity switch
        {
            "moderate" => 1,
            "high" => 2,
            "critical" => 3,
            _ => 0,
        };

    /// <summary>The finding level of a vulnerability: a warning for low and moderate, a problem for high and critical.</summary>
    public static string Level(string severity) => Rank(severity) >= 2 ? "problem" : "warn";

    /// <summary>
    /// A vulnerable-package finding per vulnerable package of the unit (its worst advisory decides the level), and
    /// with <paramref name="outdated"/> an outdated-package note per package with a newer stable version;
    /// <paramref name="statuses"/> are keyed by <see cref="Key"/>.
    /// </summary>
    public static IEnumerable<ArchitectureFinding> Findings(
        BuildUnit unit,
        IReadOnlyDictionary<string, PackageStatus> statuses,
        bool outdated
    )
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(statuses);
        return UnitFindings(unit, statuses, outdated);
    }

    private static IEnumerable<ArchitectureFinding> UnitFindings(
        BuildUnit unit,
        IReadOnlyDictionary<string, PackageStatus> statuses,
        bool outdated
    )
    {
        foreach (var package in unit.Packages)
        {
            if (
                NuGetVersion.InUse(package.Version, package.Unresolved) is not { } version
                || !statuses.TryGetValue(Key(package.Name, version.Text), out var status)
            )
            {
                continue;
            }

            if (status.Vulnerabilities is [var worst, ..])
            {
                var fixedIn = status.Vulnerabilities.Select(v => NuGetVersion.Parse(v.Fixed)).Max();
                yield return new()
                {
                    Kind = "vulnerable-package",
                    Level = Level(worst.Severity),
                    Title =
                        $"{unit.Name} uses {package.Name} {version.Text}, which has a known {worst.Severity} vulnerability",
                    Why =
                        $"{package.Name} {version.Text} falls into "
                        + string.Join(
                            "; ",
                            status.Vulnerabilities.Select(v => $"{v.Severity} advisory {v.Link} (affects {v.Versions})")
                        )
                        + ".",
                    Fix = fixedIn is null
                        ? $"Upgrade {package.Name} to a version outside the affected ranges, or replace it."
                        : $"Upgrade {package.Name} to {fixedIn.Text} or later.",
                    Project = unit.Path,
                    Evidence = [unit.Path + " (" + package.Name + " " + version.Text + ")"],
                    Key = ArchitectureAnalyzer.Key("vulnerable-package", unit.Path, package.Name),
                    Source = ArchitectureAnalyzer.Harness("internal/packages"),
                };
            }

            if (outdated && status.Latest is { } latest)
            {
                yield return new()
                {
                    Kind = "outdated-package",
                    Level = "info",
                    Title = $"{unit.Name} uses {package.Name} {version.Text}; {latest} is available",
                    Why = $"A newer stable version of {package.Name} is published on its package source.",
                    Fix = $"Upgrade {package.Name} to {latest} when it suits, after reading its release notes.",
                    Project = unit.Path,
                    Evidence = [unit.Path + " (" + package.Name + " " + version.Text + ")"],
                    Key = ArchitectureAnalyzer.Key("outdated-package", unit.Path, package.Name),
                    Source = ArchitectureAnalyzer.Harness("internal/packages"),
                };
            }
        }
    }
}
