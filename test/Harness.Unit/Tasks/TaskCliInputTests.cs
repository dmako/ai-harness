using AwesomeAssertions;
using Harness.Cli.Tasks;
using Xunit;

namespace Harness.Unit.Tasks;

public sealed class TaskCliInputTests
{
    [Theory]
    [InlineData("--1h")]
    [InlineData("++1h")]
    [InlineData("+-1h")]
    [InlineData("1hX")]
    [InlineData("9999999999999h")]
    [InlineData("١h")]
    public void InvalidGoStyleDurationIsRejected(string value) =>
        FluentActions.Invoking(() => TaskCliInput.Lease(value)).Should().ThrowExactly<TaskUsageException>();

    [Fact]
    public void GoStyleCompoundDurationsAndClrSubtickRoundingAreExplicit()
    {
        TaskCliInput.Lease("1h30m").Should().Be(TimeSpan.FromMinutes(90));
        TaskCliInput.Lease("1.5h").Should().Be(TimeSpan.FromMinutes(90));
        TaskCliInput.Lease("-1h").Should().Be(TimeSpan.FromHours(-1));
        TaskCliInput.Lease("1ns").Should().Be(TimeSpan.FromTicks(1));
        TaskCliInput.Lease("-0").Should().Be(TimeSpan.Zero);
        TaskCliInput.Lease("1ms500us").Should().Be(TimeSpan.FromMilliseconds(1.5));
    }
}
