using System.Globalization;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Harness.Cli;
using Harness.Cli.Scm;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Scm;
using Harness.Scm;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmDoctorTests : IDisposable
{
    private const string Good = "glpat-doctor-good-0123456789";
    private const string Api = "/api/v4";
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-doctor-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Global => Path.Combine(_directory, "global");

    private string Repository => Path.Combine(_directory, "repo");

    private ScmConfigurationFile Store => new(Path.Combine(Global, "scm.toml"));

    public void Dispose() => Directory.Delete(_directory, true);

    /// <summary>A GitLab that knows the good token and the project group/repo.</summary>
    private static FakeScmServer Gitlab()
    {
        var server = new FakeScmServer();
        server
            .Json("GET", Api + "/version", """{"version":"17.5.0"}""")
            .Json("GET", Api + "/user", """{"id":1,"username":"reviewer"}""")
            .Json(
                "GET",
                Api + "/projects/group%2Frepo",
                """{"id":5,"path_with_namespace":"group/repo","name":"repo"}"""
            );
        return server;
    }

    private void Repo(string? remote)
    {
        var git = Path.Combine(Repository, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(git, "objects"));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(
            Path.Combine(git, "config"),
            "[core]\n\tbare = false\n" + (remote is null ? "" : $"[remote \"origin\"]\n\turl = {remote}\n")
        );
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(FakeScmServer server, params string[] args)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var code = await CliApplication.RunConfiguredAsync(
            args,
            output,
            error,
            builder =>
            {
                builder.Services.AddSingleton(new ProjectLifecycle(Path.Combine(Global, "config.toml")));
                builder.Services.AddSingleton(new ScmHttpOptions(server));
            },
            null,
            Token
        );
        return (code, output.ToString(), error.ToString());
    }

    private async Task<Dictionary<string, (string Level, string Detail, string? Fix)>> DoctorAsync(FakeScmServer server)
    {
        var (_, output, error) = await RunAsync(server, "doctor", "--json", "--project", Repository);
        output.Should().NotContain(Good, error);
        using var json = JsonDocument.Parse(output);
        return json
            .RootElement.GetProperty("checks")
            .EnumerateArray()
            .Where(check => check.GetProperty("name").GetString()!.StartsWith("scm", StringComparison.Ordinal))
            .ToDictionary(
                check => check.GetProperty("name").GetString()!,
                check =>
                    (
                        check.GetProperty("level").GetString()!,
                        check.GetProperty("detail").GetString()!,
                        check.TryGetProperty("fix", out var fix) ? fix.GetString() : null
                    )
            );
    }

    [Fact]
    public async Task NoScmIsInformationNotAFailure()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        using var server = Gitlab();

        var checks = await DoctorAsync(server);

        checks.Should().ContainSingle();
        checks["scm"].Level.Should().Be("info");
        checks["scm"].Detail.Should().Contain("harness scm add");
        var text = await RunAsync(server, "scm", "test");
        text.Code.Should().Be(0);
        text.Output.Should().Contain("No SCM configured");
    }

    [Fact]
    public async Task AReachableProjectOnAWorkingScmIsHealthy()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Good, null), Token);
        using var server = Gitlab();

        var checks = await DoctorAsync(server);

        checks["scm"].Level.Should().Be("ok");
        checks["scm:work"]
            .Should()
            .Be(("ok", "GitLab https://gitlab.example.com: version 17.5.0, signed in as reviewer", null));
        checks["scm project"].Level.Should().Be("ok");
        checks["scm project"].Detail.Should().Be("work (gitlab) group/repo from remote origin: reachable (group/repo)");
        server.Requests.Should().AllSatisfy(request => request.Headers["PRIVATE-TOKEN"].Should().Be(Good));
    }

    [Fact]
    public async Task StoredTokensAreCheckedForEncryptionAndTheirKeyFolder()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        Directory.CreateDirectory(Global);
        var file = Path.Combine(Global, "scm.toml");
        await File.WriteAllTextAsync(
            file,
            $"[scm.work]\ntype = \"gitlab\"\nurl = \"https://gitlab.example.com\"\ntoken = \"{Good}\"\n",
            Token
        );
        using var server = Gitlab();

        var plain = await DoctorAsync(server);
        plain["scm keys"].Level.Should().Be("warn");
        plain["scm keys"].Detail.Should().Contain("unencrypted").And.Contain("work");
        plain["scm keys"].Fix.Should().Contain("harness scm set work --token-stdin");
        plain["scm:work"].Level.Should().Be("ok");

        await Store.UpdateAsync("work", new ScmEntryChange(Type: "gitlab"), Token);
        var encrypted = await DoctorAsync(server);
        encrypted["scm keys"].Level.Should().Be("ok");
        encrypted["scm keys"].Detail.Should().Contain("encrypted");
        (await File.ReadAllTextAsync(file, Token)).Should().NotContain(Good);

        Directory.Delete(Path.Combine(Global, "scm-keys"), true);
        var lost = await DoctorAsync(server);
        lost["scm keys"].Level.Should().Be("fail");
        lost["scm keys"].Detail.Should().Contain("missing");
        lost["scm:work"].Level.Should().Be("fail");
        lost["scm:work"].Detail.Should().Contain("cannot be decrypted");
        lost["scm:work"].Fix.Should().Contain("harness scm set work --token-stdin");
    }

    [Fact]
    public async Task AGithubProjectIsCheckedThroughTheGithubApi()
    {
        Repo("git@github.com:owner/repo.git");
        await Store.AddAsync(new("hub", "github", "https://github.com", Good, null), Token);
        using var server = new FakeScmServer();
        server
            .Json("GET", "/meta", """{"verifiable_password_authentication":true}""")
            .Json("GET", "/user", """{"id":1,"login":"reviewer"}""")
            .Json("GET", "/repos/owner/repo", """{"id":7,"full_name":"owner/repo","name":"repo","private":true}""");

        var checks = await DoctorAsync(server);

        checks["scm:hub"]
            .Should()
            .Be(("ok", "GitHub https://github.com: version github.com, signed in as reviewer", null));
        checks["scm project"].Level.Should().Be("ok");
        checks["scm project"].Detail.Should().Be("hub (github) owner/repo from remote origin: reachable (owner/repo)");
        server.Requests.Should().AllSatisfy(request => request.Uri.Host.Should().Be("api.github.com"));
        server.Requests.Should().AllSatisfy(request => request.Headers["Authorization"].Should().Be("Bearer " + Good));
        var test = await RunAsync(server, "scm", "test");
        test.Code.Should().Be(0, test.Error);
        test.Output.Should().Contain("[ok  ] scm:hub");
    }

    [Fact]
    public async Task FailedConnectionsMissingTokensAndUnreachableProjectsSayWhatToFix()
    {
        Repo("https://gitlab.example.com/group/other.git");
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Good, null), Token);
        await Store.AddAsync(new("forge", "forgejo", "https://git.example.org", Good, null), Token);
        await Store.AddAsync(
            new("env", "gitlab", "https://env.example.com", null, "HARNESS_TEST_UNSET_SCM_TOKEN"),
            Token
        );
        using var server = Gitlab();

        var checks = await DoctorAsync(server);

        checks["scm:forge"].Level.Should().Be("fail");
        checks["scm:forge"].Detail.Should().Contain("wrong_server");
        checks["scm:forge"].Fix.Should().Contain("type");
        checks["scm:env"].Level.Should().Be("fail");
        checks["scm:env"].Detail.Should().Contain("HARNESS_TEST_UNSET_SCM_TOKEN");
        checks["scm:env"].Fix.Should().Contain("Set HARNESS_TEST_UNSET_SCM_TOKEN");
        checks["scm project"].Level.Should().Be("fail");
        checks["scm project"].Detail.Should().Contain("group/other").And.Contain("not_found");
        checks["scm project"].Fix.Should().Contain("[scm] project");

        var test = await RunAsync(server, "scm", "test");
        test.Code.Should().Be(1);
        test.Output.Should().Contain("[ok  ] scm:work").And.Contain("[fail] scm:forge").And.Contain("fix: ");
        (test.Output + test.Error).Should().NotContain(Good);
    }

    [Fact]
    public async Task RejectedTokensFailTheConnectionAndSkipTheProjectCheck()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Good, null), Token);
        using var server = new FakeScmServer();
        server.On(
            "GET",
            Api + "/version",
            new FakeResponse(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""")
        );

        var checks = await DoctorAsync(server);

        checks["scm:work"].Level.Should().Be("fail");
        checks["scm:work"].Detail.Should().Contain("unauthorized");
        checks["scm:work"].Fix.Should().Contain("harness scm set");
        checks["scm project"].Level.Should().Be("warn");
        checks["scm project"].Detail.Should().Contain("failed its connection test");
    }

    [Fact]
    public async Task UnmatchedAndMissingRemotesAreReported()
    {
        Repo("git@github.com:me/repo.git");
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Good, null), Token);
        using var server = Gitlab();

        var unmatched = await DoctorAsync(server);
        unmatched["scm project"].Level.Should().Be("warn");
        unmatched["scm project"]
            .Detail.Should()
            .Contain("origin git@github.com:me/repo.git")
            .And.Contain("work https://gitlab.example.com");
        unmatched["scm project"].Fix.Should().Contain("[scm]");

        Repo(null);
        (await DoctorAsync(server))["scm project"].Level.Should().Be("info");
    }

    [Fact]
    public async Task UnreadableAndTooOpenScmFilesAreReported()
    {
        Repo(null);
        using var server = Gitlab();
        Directory.CreateDirectory(Global);
        var path = Path.Combine(Global, "scm.toml");
        await File.WriteAllTextAsync(path, $"[scm.work\ntoken = \"{Good}\"\n", Token);

        var broken = await DoctorAsync(server);
        broken["scm"].Level.Should().Be("fail");
        broken["scm"].Detail.Should().Contain("not valid TOML");

        // written by hand: the folder's inherited ACL on Windows, a group/other-readable mode elsewhere
        await File.WriteAllTextAsync(
            path,
            $"[scm.work]\ntype = \"gitlab\"\nurl = \"https://gitlab.example.com\"\ntoken = \"{Good}\"\n",
            Token
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }

        var open = await DoctorAsync(server);
        open["scm"].Level.Should().Be("warn");
        open["scm"].Fix.Should().Contain("owner-only");

        await Store.UpdateAsync("work", new ScmEntryChange(Type: "gitlab"), Token);
        (await DoctorAsync(server))["scm"].Level.Should().Be("ok");
    }

    [Fact]
    public async Task OneScmIsTestedByNameWithJsonOutput()
    {
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Good, null), Token);
        await Store.AddAsync(new("forge", "forgejo", "https://git.example.org", Good, null), Token);
        using var server = Gitlab();

        var one = await RunAsync(server, "scm", "test", "work", "--json");

        one.Code.Should().Be(0, one.Error);
        using var json = JsonDocument.Parse(one.Output);
        var result = json.RootElement.GetProperty("scm").EnumerateArray().Single();
        result.GetProperty("name").GetString().Should().Be("work");
        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        result.GetProperty("version").GetString().Should().Be("17.5.0");
        result.GetProperty("user").GetString().Should().Be("reviewer");
        one.Output.Should().NotContain(Good);

        var missing = await RunAsync(server, "scm", "test", "nope");
        missing.Code.Should().Be(1);
        missing.Error.Should().Contain("No SCM is named 'nope'");
    }
}
