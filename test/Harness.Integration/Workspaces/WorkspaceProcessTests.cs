using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Integration.Providers;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Harness.Integration.Workspaces;

[Collection("Provider processes")]
public sealed class WorkspaceProcessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Binary =>
        Path.Combine(
            ProviderFixture.RepositoryRoot,
            "src",
            "Harness.Cli",
            "bin",
            Directory.GetParent(ProviderFixture.Worker)!.Parent!.Name,
            "net10.0",
            "harness.dll"
        );

    [Fact]
    public async Task ManagementCommandsAndOverviewPreserveMemberDatabases()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        var before = fixture.Hashes();
        (await RunAsync(fixture, "workspace")).Output.Should().Contain("No workspaces yet");
        (
            await RunAsync(
                fixture,
                "workspace",
                "create",
                "example",
                "--discover",
                Path.GetTempPath(),
                "--title",
                "Example workspace"
            )
        )
            .Output.Should()
            .Contain("Joined 3 project(s)");
        (await RunAsync(fixture, "workspaces")).Output.Should().Contain("example").And.Contain("Example workspace");
        (await RunAsync(fixture, "workspace", "show", "example"))
            .Output.Should()
            .Contain("alpha")
            .And.Contain("beta")
            .And.Contain("gamma");
        (await RunAsync(fixture, "workspace", "join", "example", "--project", fixture.Members[0].Root))
            .Output.Should()
            .Contain("already in");
        (await RunAsync(fixture, "workspace", "rename", "example", "--to", "renamed", "-clear-title"))
            .Output.Should()
            .Contain("Workspace renamed");
        (
            await RunAsync(
                fixture,
                "projects",
                "rename",
                "--project",
                fixture.Members[0].Root,
                "--to",
                "Alpha",
                "--title",
                "Main library"
            )
        )
            .Output.Should()
            .Contain("short name Alpha");
        using (var projects = JsonDocument.Parse((await RunAsync(fixture, "projects", "--json")).Output))
        {
            var first = projects.RootElement.GetProperty("projects")[0];
            first.GetProperty("name").GetString().Should().Be("Alpha");
            first.GetProperty("workspaces")[0].GetString().Should().Be("renamed");
            first.GetProperty("live").GetProperty("state").GetString().Should().Be("ok");
            first.GetProperty("live").GetProperty("stale").GetString().Should().Contain("never been synced");
        }
        (await RunAsync(fixture, "projects"))
            .Output.Should()
            .Contain("context")
            .And.Contain("harness")
            .And.Contain("renamed");
        (await RunAsync(fixture, "workspace", "leave", "renamed", "--project", fixture.Members[1].Root))
            .Output.Should()
            .Contain("left renamed");
        (await RunAsync(fixture, "workspace", "delete", "renamed")).Output.Should().Contain("state are untouched");
        (await RunAsync(fixture, "projects", "forget", fixture.Members[2].Id)).Output.Should().Contain("Forgot");
        fixture.Hashes().Should().Equal(before);
    }

    [Fact]
    public async Task PublicMcpScopesPreserveProvenanceBudgetAndLightStartup()
    {
        using var fixture = await WorkspaceFixture.CreateAsync();
        foreach (var repo in fixture.Repositories)
        {
            repo.Write(
                "harness_provider_info.json",
                """
                {"protocol":"harness-provider/1","name":"fixture","capabilities":[{"capability":"code.symbol_search","evidence":"exact"}]}
                """
            );
            repo.Write(
                "code_symbol_search.json",
                """
                {"items":[{"kind":"symbol","name":"Widget","path":"a.cs","start_line":1}]}
                """
            );
            repo.Write(
                ".harness/heavy-fixture/harness_provider_info.json",
                """
                {"protocol":"harness-provider/1","name":"heavy","capabilities":[{"capability":"code.symbol_search"}]}
                """
            );
            repo.Write(".harness/heavy-fixture/code_symbol_search.json", """{"items":[]}""");
            repo.Write(
                ".harness/config.toml",
                $"""
                [providers.fixture]
                type = 'harness'
                command = 'dotnet'
                args = [{JsonSerializer.Serialize(
                    ProviderFixture.Worker.Replace('\\', '/')
                )}, 'normal', {JsonSerializer.Serialize(repo.Root.Replace('\\', '/'))}]
                dir = 'project'
                [providers.heavy]
                type = 'harness'
                command = 'dotnet'
                args = [{JsonSerializer.Serialize(
                    ProviderFixture.Worker.Replace('\\', '/')
                )}, 'normal', {JsonSerializer.Serialize(
                    Path.Combine(repo.Root, ".harness", "heavy-fixture").Replace('\\', '/')
                )}]
                dir = 'project' 
                context = false
                """
            );
        }
        await fixture.Store.CreateAsync("one", Token);
        await fixture.Store.JoinAsync("one", fixture.Members.Take(2).Select(member => member.Id).ToArray(), Token);
        await fixture.Store.CreateAsync("two", Token);
        await fixture.Store.JoinAsync("two", [fixture.Members[2].Id], Token);
        var foreignHash = fixture.Hashes()[1];
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HARNESS_CONFIG_DIR"] = fixture.Global;
        environment["GIT_CONFIG_NOSYSTEM"] = "1";
        environment["GIT_CONFIG_GLOBAL"] = Path.Combine(fixture.Global, "absent.gitconfig");
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(
                new()
                {
                    Command = "dotnet",
                    Arguments = [Binary, "mcp", "--project", fixture.Members[0].Root],
                    WorkingDirectory = fixture.Members[0].Root,
                    EnvironmentVariables = environment,
                    InheritEnvironmentVariables = false,
                }
            ),
            cancellationToken: Token
        );
        var search = await client.CallToolAsync(
            "search_project",
            new Dictionary<string, object?>
            {
                ["query"] = "Widget",
                ["kind"] = "symbol",
                ["scope"] = "workspace",
            },
            cancellationToken: Token
        );
        search.IsError.Should().BeNull();
        var result = search.StructuredContent!.Value;
        result
            .GetProperty("results")
            .EnumerateArray()
            .Select(item => item.GetProperty("project").GetString())
            .Should()
            .Equal("alpha", "beta");
        result.TryGetProperty("errors", out _).Should().BeFalse();
        var context = await client.CallToolAsync(
            "get_context",
            new Dictionary<string, object?>
            {
                ["task"] = "Adjust Widget",
                ["scope"] = "workspace",
                ["budget"] = 1500,
            },
            cancellationToken: Token
        );
        context.IsError.Should().BeNull();
        var text = context.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>().Which.Text;
        Encoding.UTF8.GetByteCount(text).Should().BeLessThanOrEqualTo(1500);
        context
            .StructuredContent!.Value.GetProperty("workspace")[0]
            .GetProperty("project")
            .GetString()
            .Should()
            .Be("beta");
        text.Should().NotContain("gamma");
        foreach (var repo in fixture.Repositories)
        {
            File.Exists(Path.Combine(repo.Root, ".harness", "heavy-fixture", "harness_provider_info.args"))
                .Should()
                .BeFalse();
        }

        var deep = await client.CallToolAsync(
            "search_project",
            new Dictionary<string, object?>
            {
                ["query"] = "Widget",
                ["kind"] = "symbol",
                ["scope"] = "workspace",
                ["path"] = "beta:a.cs#Widget",
                ["depth"] = "deep",
            },
            cancellationToken: Token
        );
        deep.IsError.Should().BeNull();
        deep.StructuredContent!.Value.GetProperty("results")
            .EnumerateArray()
            .Should()
            .OnlyContain(item => item.GetProperty("project").GetString() == "beta");
        File.Exists(
                Path.Combine(fixture.Repositories[1].Root, ".harness", "heavy-fixture", "harness_provider_info.args")
            )
            .Should()
            .BeTrue();
        fixture.Hashes()[1].Should().Be(foreignHash);
    }

    [SuppressMessage(
        "Security",
        "S4036:Searching OS commands in PATH is security-sensitive",
        Justification = "The CLI integration fixture runs dotnet directly without a shell."
    )]
    private static async Task<(string Output, string Error)> RunAsync(WorkspaceFixture fixture, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = fixture.Global,
            },
        };
        foreach (var arg in new[] { Binary }.Concat(args))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.StartInfo.Environment["HARNESS_CONFIG_DIR"] = fixture.Global;
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        try
        {
            await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(60), Token);
            var stderr = await error;
            process.ExitCode.Should().Be(0, stderr);
            return (await output, stderr);
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
