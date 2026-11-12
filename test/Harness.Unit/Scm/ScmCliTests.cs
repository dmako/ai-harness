using System.Globalization;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Harness.Cli;
using Harness.Cli.Scm;
using Harness.Infrastructure.Projects;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmCliTests : IDisposable
{
    private const string Secret = "glpat-CliSecret0123456789";
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-cli-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, true);

    private async Task<(int Code, string Output, string Error)> RunAsync(string stdin, params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await CliApplication.RunConfiguredAsync(
            args,
            output,
            error,
            builder =>
            {
                builder.Services.AddSingleton(new ProjectLifecycle(Path.Combine(_directory, "config.toml")));
                builder.Services.AddSingleton(new CliInput(() => new StringReader(stdin)));
            },
            null,
            Token
        );
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task AddListSetAndRemoveManageTheMachineFileWithoutShowingTokens()
    {
        var add = await RunAsync(
            Secret + "\n",
            "scm",
            "add",
            "work",
            "--type",
            "gitlab",
            "--url",
            "https://gitlab.example.com/",
            "--token-stdin"
        );
        add.Code.Should().Be(0, add.Error);
        add.Output.Should().Contain("Added SCM work (GitLab, https://gitlab.example.com), token ****6789.");
        (await File.ReadAllTextAsync(Path.Combine(_directory, "scm.toml"), Token)).Should().Contain(Secret);

        var list = await RunAsync("", "scm", "list");
        list.Output.Should().MatchRegex(@"work\s+gitlab\s+https://gitlab\.example\.com\s+token \*\*\*\*6789");

        var json = await RunAsync("", "scm", "list", "--json");
        var entry = JsonNode.Parse(json.Output)!["scm"]![0]!;
        entry["name"]!.GetValue<string>().Should().Be("work");
        entry["type"]!.GetValue<string>().Should().Be("gitlab");
        entry["token_source"]!.GetValue<string>().Should().Be("stored");
        entry["token"]!.GetValue<string>().Should().Be("****6789");
        entry["token_available"]!.GetValue<bool>().Should().BeTrue();

        var set = await RunAsync(
            "",
            "scm",
            "set",
            "work",
            "--rename",
            "corp",
            "--url",
            "https://gitlab.example.com/gitlab",
            "--token-env",
            "CORP_TOKEN_UNSET_FOR_TEST"
        );
        set.Code.Should().Be(0, set.Error);
        set.Output.Should().Contain("Updated SCM corp").And.Contain("token from $CORP_TOKEN_UNSET_FOR_TEST (not set)");

        var remove = await RunAsync("", "scm", "remove", "CORP");
        remove.Code.Should().Be(0, remove.Error);
        (await RunAsync("", "scm", "list")).Output.Should().Contain("No SCM configured");

        foreach (var run in new[] { add, list, json, set, remove })
        {
            (run.Output + run.Error).Should().NotContain(Secret);
        }
    }

    [Fact]
    public async Task TokensOnTheCommandLineWorkButWarnAboutShellHistory()
    {
        var add = await RunAsync(
            "",
            "scm",
            "add",
            "f",
            "--type",
            "forgejo",
            "--url",
            "http://localhost:3000",
            "--token",
            Secret
        );

        add.Code.Should().Be(0, add.Error);
        add.Error.Should().Contain("shell history").And.NotContain(Secret);
        add.Output.Should().NotContain(Secret);
    }

    [Fact]
    public async Task GithubIsAnAcceptedType()
    {
        var add = await RunAsync(
            "",
            "scm",
            "add",
            "hub",
            "--type",
            "github",
            "--url",
            "https://github.com",
            "--token-env",
            "GH_TOKEN"
        );

        add.Code.Should().Be(0, add.Error);
        add.Output.Should().Contain("GitHub");
        (await RunAsync("", "scm", "set", "hub", "--type", "github", "--url", "https://ghe.example.com"))
            .Code.Should()
            .Be(0);
    }

    [Theory]
    [InlineData(new[] { "scm", "add", "x", "--type", "gitlab", "--url", "https://h" }, "token is required")]
    [InlineData(
        new[] { "scm", "add", "x", "--type", "gitlab", "--url", "https://h", "--token-stdin", "--token-env", "V" },
        "only one of"
    )]
    [InlineData(new[] { "scm", "add", "x", "--type", "gitlab", "--url", "nope", "--token-env", "V" }, "http")]
    [InlineData(new[] { "scm", "set", "missing", "--type", "gitlab" }, "No SCM is named 'missing'")]
    [InlineData(new[] { "scm", "set", "missing" }, "Nothing to change")]
    [InlineData(new[] { "scm", "remove", "missing" }, "No SCM is named 'missing'")]
    public async Task FailuresGoToStderrWithExitOne(string[] args, string message)
    {
        var result = await RunAsync("", args);

        result.Code.Should().Be(1);
        result.Error.Should().Contain("harness scm").And.Contain(message);
        result.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownTypesAndMissingOptionsAreParseErrors()
    {
        (await RunAsync("", "scm", "add", "x", "--type", "bitbucket", "--url", "https://h", "--token-env", "V"))
            .Code.Should()
            .Be(2);
        (await RunAsync("", "scm", "add", "x", "--url", "https://h", "--token-env", "V")).Code.Should().Be(2);
    }

    [Fact]
    public async Task EmptyStandardInputIsRefused()
    {
        var result = await RunAsync("\n", "scm", "add", "x", "--type", "gitlab", "--url", "https://h", "--token-stdin");

        result.Code.Should().Be(1);
        result.Error.Should().Contain("No token was read from standard input");
    }

    [Fact]
    public async Task ScmAppearsInHelpAndCompletion()
    {
        (await RunAsync("", "--help")).Output.Should().MatchRegex(@"(?m)^\s*scm\s");

        var top = await RunAsync("", "__complete", "bash", "sc");
        top.Output.Should().Contain("scm");
        var verbs = await RunAsync("", "__complete", "bash", "scm", "");
        verbs.Output.Should().Contain("list").And.Contain("add").And.Contain("set").And.Contain("remove");
    }
}
