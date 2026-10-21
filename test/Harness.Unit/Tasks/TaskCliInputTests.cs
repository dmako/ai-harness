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
    public void InvalidGoStyleDurationIsRejected(string value) => Assert.Throws<TaskUsageException>(() => TaskCliInput.Lease(value));

    [Fact]
    public void GoStyleCompoundDurationsAndClrSubtickRoundingAreExplicit()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), TaskCliInput.Lease("1h30m"));
        Assert.Equal(TimeSpan.FromMinutes(90), TaskCliInput.Lease("1.5h"));
        Assert.Equal(TimeSpan.FromHours(-1), TaskCliInput.Lease("-1h"));
        Assert.Equal(TimeSpan.FromTicks(1), TaskCliInput.Lease("1ns"));
        Assert.Equal(TimeSpan.Zero, TaskCliInput.Lease("-0"));
        Assert.Equal(TimeSpan.FromMilliseconds(1.5), TaskCliInput.Lease("1ms500us"));
    }
}
