using AwesomeAssertions;
using Harness.Core.Reports;
using Harness.Core.Telemetry;
using Xunit;

namespace Harness.Unit.Telemetry;

public sealed class ReportAudienceTests
{
    private static LocalReport Report() =>
        new()
        {
            Id = "prj_1",
            Days = 30,
            Context = new()
            {
                Calls = 2,
                Rows =
                [
                    new()
                    {
                        Provider = "ripwire",
                        Capability = "code.search",
                        Language = "csharp",
                    },
                ],
                Providers = new() { ["ripwire"] = [1, 2] },
                Misses = [new() { Path = "src/a.cs" }],
            },
            Knowledge = new() { Pending = 1 },
        };

    [Fact]
    public void FindingsSayWhetherTheUserOrTheHarnessDeveloperActsOnThem()
    {
        var findings = ReportAnalyzer.Findings(Report(), DateTimeOffset.UtcNow, [])!;

        findings.Should().OnlyContain(f => f.Audience == ReportAudience.User || f.Audience == ReportAudience.Developer);
        // too few scored answers to judge relevance: how harness gathers, the developer's
        findings
            .Single(f => f.Message.StartsWith("only 2 get_context", StringComparison.Ordinal))
            .Audience.Should()
            .Be(ReportAudience.Developer);
        // no client hooks and a proposal waiting: the user's to act on
        findings.Single(f => f.Area is "hooks").Audience.Should().Be(ReportAudience.User);
        findings.Single(f => f.Area is "knowledge").Audience.Should().Be(ReportAudience.User);
        findings.Should().NotContain(f => (f.Action ?? "").Contains("harness metrics", StringComparison.Ordinal));
        findings.Should().NotContain(f => (f.Action ?? "").Contains("context stats", StringComparison.Ordinal));
    }

    [Fact]
    public void TheUsersReportLeavesOutTheDevelopersFindingsAndTuningNumbers()
    {
        var report = Report();
        report.Findings = ReportAnalyzer.Findings(report, DateTimeOffset.UtcNow, []);

        var own = report.ForUser();

        own.Findings.Should().NotBeEmpty().And.OnlyContain(f => f.Audience == ReportAudience.User);
        own.Context.Rows.Should().BeNull();
        own.Context.Misses.Should().BeNull();
        own.Context.Providers.Should().BeEmpty();
        own.Context.Calls.Should().Be(2, "the user's own numbers stay");
        report.Findings.Should().Contain(f => f.Audience == ReportAudience.Developer, "the full report is unchanged");
        report.Context.Rows.Should().NotBeNull();
    }
}
