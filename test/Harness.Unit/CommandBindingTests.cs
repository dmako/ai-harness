using System.CommandLine;
using AwesomeAssertions;
using Harness.Cli;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harness.Unit;

public sealed class CommandBindingTests
{
    [Theory]
    [InlineData("add", "--accept")]
    [InlineData("add", "--depends")]
    [InlineData("add", "--scope")]
    [InlineData("list", "--status")]
    [InlineData("complete", "--acc")]
    [InlineData("complete", "--check")]
    public void ArrayOptionsRequireAValuePerOccurrenceAndAccumulateRepeatedFlags(string verb, string flag)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var root = CliCommands.Create(services, TextWriter.Null);
        var command = root.Subcommands.Single(c => c.Name is "tasks").Subcommands.Single(c => c.Name == verb);
        var option = command.Options.OfType<Option<string[]>>().Single(o => o.Name == flag);
        string[] prefix = verb is "complete" ? ["tasks", verb, "TASK-1"] : ["tasks", verb];

        var parsed = root.Parse([.. prefix, flag, "first", flag, "second"]);
        parsed.Errors.Should().BeEmpty();
        parsed.GetValue(option).Should().Equal("first", "second");
        root.Parse([.. prefix, flag]).Errors.Should().NotBeEmpty();
        root.Parse([.. prefix, flag, "first", "second"]).Errors.Should().NotBeEmpty();
    }
}
