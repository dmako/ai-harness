using AwesomeAssertions;
using Harness.Cli.Ui;
using Harness.Infrastructure.Projects;
using Xunit;

namespace Harness.Unit;

public sealed class HarnessFixTests
{
    [Theory]
    [InlineData(
        "harness install --client claude-code (or see: harness docs connecting)",
        "install --client claude-code"
    )]
    [InlineData("run 'harness sync'", "sync")]
    [InlineData("run 'harness sync' (the git hooks do) to measure HEAD", "sync")]
    [InlineData(
        "harness hooks install --kind claude-code,codex (or run 'harness hooks')",
        "hooks install --kind claude-code,codex"
    )]
    [InlineData("harness instructions", "instructions")]
    public void AFixNamingAnAllowedHarnessCommandRunsItsPlainArguments(string fix, string arguments) =>
        HarnessFix.Command(fix).Should().Equal(arguments.Split(' '));

    [Theory]
    [InlineData("run 'harness init'")]
    [InlineData("harness uninstall --all --delete-data")]
    [InlineData("harness hooks uninstall")]
    [InlineData("correct the command path in C:\\Users\\x\\.claude.json")]
    [InlineData("harness sync && rm -rf /")]
    [InlineData("harness sync $(whoami)")]
    [InlineData("harness install --command \"evil\"")]
    [InlineData("harness")]
    public void EveryOtherFixIsTheUsersToDo(string fix) => HarnessFix.Command(fix).Should().BeNull();

    [Fact]
    public void OnlyAFailingOrWarningCheckWithARunnableFixIsFixable()
    {
        HarnessFix
            .Fixable(new("claude-code", "warn", "not configured", "harness install --client claude-code"))
            .Should()
            .BeTrue();
        HarnessFix
            .Fixable(new("claude-code", "ok", "configured", "harness install --client claude-code"))
            .Should()
            .BeFalse();
        HarnessFix.Fixable(new("project", "warn", "not initialized", "run 'harness init'")).Should().BeFalse();
        HarnessFix.Fixable(new("config", "fail", "bad key")).Should().BeFalse();
    }
}
