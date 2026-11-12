using System.Globalization;
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

/// <summary>
/// Choosing a project's SCM when several connections serve its server (TASK-0132): resolution order, the choice kept
/// in scm.toml per repository, and 'harness scm use|which'.
/// </summary>
public sealed class ScmProjectChoiceTests : IDisposable
{
    private const string Secret = "glpat-choice-0123456789";
    private static readonly ScmEndpoint Work = Endpoint("work", ScmKind.Gitlab, "https://gitlab.example.com");
    private static readonly ScmEndpoint Bot = Endpoint("bot", ScmKind.Gitlab, "https://gitlab.example.com");
    private static readonly ScmEndpoint Forge = Endpoint("forge", ScmKind.Forgejo, "https://git.example.org");
    private static readonly ScmRemote[] Remotes = [new("origin", "git@gitlab.example.com:group/repo.git")];
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-choice-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Global => Path.Combine(_directory, "global");

    private string Repository => Path.Combine(_directory, "repo");

    private ScmConfigurationFile Store => new(Path.Combine(Global, "scm.toml"));

    public void Dispose() => Directory.Delete(_directory, true);

    private static ScmEndpoint Endpoint(string name, ScmKind kind, string url)
    {
        ScmEndpoint.TryNormalizeUrl(url, out var normalized, out _);
        return new ScmEndpoint(name, kind, normalized);
    }

    [Fact]
    public void TwoConnectionsForOneServerAreAmbiguousUntilOneIsChosenOnThisMachine()
    {
        var auto = ScmProjectMatcher.Resolve([Work, Bot, Forge], Remotes);
        auto.Status.Should().Be(ScmResolutionStatus.Ambiguous);
        auto.Source.Should().Be(ScmChoiceSource.Automatic);
        auto.Candidates.Should().Equal(Work, Bot);
        auto.Reason.Should().Contain("harness scm use");
        auto.Describe().Should().Contain("source", "auto");
        ((string[])auto.Describe()["candidates"]!).Should().Equal("work", "bot");

        var chosen = ScmProjectMatcher.Resolve([Work, Bot, Forge], Remotes, new ScmProjectOverride(Choice: "BOT"));
        chosen.Status.Should().Be(ScmResolutionStatus.Matched);
        chosen.Match!.Endpoint.Should().Be(Bot);
        chosen.Match.Project.Should().Be("group/repo");
        chosen.Remote.Should().Be("origin");
        chosen.Source.Should().Be(ScmChoiceSource.Machine);
        chosen.Overridden.Should().BeFalse();
        chosen.Candidates.Should().Equal(Work, Bot);
        chosen.Summary().Should().Be("bot (gitlab) group/repo from remote origin, chosen on this machine");
        chosen.Describe().Should().Contain("source", "machine");

        // a single connection is matched as before, with itself as the only candidate
        var single = ScmProjectMatcher.Resolve([Work, Forge], Remotes);
        single.Match!.Endpoint.Should().Be(Work);
        single.Candidates.Should().Equal(Work);
        single.Summary().Should().Be("work (gitlab) group/repo from remote origin");
    }

    [Fact]
    public void TheProjectConfigurationWinsOverTheMachineChoiceWhichKeepsItsProjectAndRemote()
    {
        var configured = ScmProjectMatcher.Resolve(
            [Work, Bot],
            Remotes,
            new ScmProjectOverride(Name: "work", Choice: "bot")
        );
        configured.Match!.Endpoint.Should().Be(Work);
        configured.Source.Should().Be(ScmChoiceSource.Configuration);
        configured.Overridden.Should().BeTrue();
        configured.Summary().Should().EndWith(", set in .harness/config.toml");

        var fixedProject = ScmProjectMatcher.Resolve(
            [Work, Bot],
            Remotes,
            new ScmProjectOverride(Project: "other/path", Choice: "bot")
        );
        fixedProject.Match!.Endpoint.Should().Be(Bot);
        fixedProject.Match.Project.Should().Be("other/path");
        fixedProject.Source.Should().Be(ScmChoiceSource.Machine);
    }

    [Fact]
    public void AStaleChoiceIsReportedWithTheFixInsteadOfIgnored()
    {
        var missing = ScmProjectMatcher.Resolve([Work, Bot], Remotes, new ScmProjectOverride(Choice: "gone"));
        missing.Status.Should().Be(ScmResolutionStatus.Invalid);
        missing.Source.Should().Be(ScmChoiceSource.Machine);
        missing.Reason.Should().Contain("'gone'").And.Contain("harness scm use --auto");
        missing.Candidates.Should().Equal(Work, Bot);

        var elsewhere = ScmProjectMatcher.Resolve([Work, Forge], Remotes, new ScmProjectOverride(Choice: "forge"));
        elsewhere.Status.Should().Be(ScmResolutionStatus.Invalid);
        elsewhere.Reason.Should().Contain("does not serve").And.Contain("origin").And.Contain("harness scm use");
    }

    [Fact]
    public async Task ChoicesAreKeptPerRepositoryAndFollowRenamesAndRemovals()
    {
        var store = Store;
        await store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        await store.AddAsync(new("bot", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        var common = Path.Combine(Repository, ".git");

        store.ChoiceFor(common).Should().BeNull();
        await store.SetChoiceAsync(common + Path.DirectorySeparatorChar, "BOT", Token);
        Store.ChoiceFor(common).Should().Be("bot");
        (await File.ReadAllTextAsync(store.FilePath, Token)).Should().Contain("[project.").And.Contain("scm = \"bot\"");

        var unknown = () => store.SetChoiceAsync(common, "nope", Token);
        (await unknown.Should().ThrowAsync<ScmConfigurationException>()).Which.Message.Should().Contain("work, bot");

        await store.UpdateAsync("bot", new ScmEntryChange(Rename: "robot"), Token);
        Store.ChoiceFor(common).Should().Be("robot");

        await store.RemoveAsync("robot", Token);
        Store.ChoiceFor(common).Should().BeNull();
        (await File.ReadAllTextAsync(store.FilePath, Token)).Should().NotContain("[project.");

        await store.SetChoiceAsync(common, "work", Token);
        await store.SetChoiceAsync(common, null, Token);
        Store.ChoiceFor(common).Should().BeNull();
    }

    [Fact]
    public async Task TheResolverAppliesTheRepositorysChoice()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        var store = Store;
        await store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        await store.AddAsync(new("bot", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        var resolver = new ScmProjectResolver(store, _directory);

        (await resolver.ResolveAsync(Repository, null, Token)).Status.Should().Be(ScmResolutionStatus.Ambiguous);

        await store.SetChoiceAsync(Path.Combine(Repository, ".git"), "work", Token);
        var chosen = await resolver.ResolveAsync(Path.Combine(Repository, "src"), null, Token);
        chosen.Match!.Endpoint.Name.Should().Be("work");
        chosen.Source.Should().Be(ScmChoiceSource.Machine);
    }

    [Fact]
    public async Task WhichShowsTheCandidatesAndUseChoosesOrForgetsAnScm()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        await Store.AddAsync(new("bot", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        await Store.AddAsync(new("forge", "forgejo", "https://git.example.org", Secret, null), Token);

        var ambiguous = await RunAsync("scm", "which", "--project", Repository);
        ambiguous.Code.Should().Be(1);
        ambiguous.Output.Should().Contain("matches several SCMs").And.Contain("candidates: work, bot");

        var use = await RunAsync("scm", "use", "bot", "--project", Repository);
        use.Code.Should().Be(0, use.Error);
        use.Output.Should().Contain("uses SCM bot on this machine").And.Contain("chosen:     chosen on this machine");
        Store.ChoiceFor(Path.Combine(Repository, ".git")).Should().Be("bot");
        (await RunAsync("status", "--project", Repository))
            .Output.Should()
            .Contain("SCM         bot (gitlab) group/repo from remote origin, chosen on this machine");

        var json = await RunAsync("scm", "which", "--project", Repository, "--json");
        json.Code.Should().Be(0, json.Error);
        using (var document = JsonDocument.Parse(json.Output))
        {
            var scm = document.RootElement.GetProperty("scm");
            scm.GetProperty("name").GetString().Should().Be("bot");
            scm.GetProperty("source").GetString().Should().Be("machine");
            document
                .RootElement.GetProperty("candidates")
                .EnumerateArray()
                .Select(candidate => candidate.GetProperty("name").GetString())
                .Should()
                .Equal("work", "bot");
            json.Output.Should().NotContain(Secret);
        }

        // a connection that does not serve the repository is refused, and the choice stays
        var wrong = await RunAsync("scm", "use", "forge", "--project", Repository);
        wrong.Code.Should().Be(1);
        wrong.Error.Should().Contain("does not serve");
        Store.ChoiceFor(Path.Combine(Repository, ".git")).Should().Be("bot");

        var auto = await RunAsync("scm", "use", "--auto", "--project", Repository);
        auto.Code.Should().Be(0, auto.Error);
        auto.Output.Should().Contain("detected from the remotes again");
        Store.ChoiceFor(Path.Combine(Repository, ".git")).Should().BeNull();

        (await RunAsync("scm", "use", "--project", Repository)).Code.Should().Be(2);
        (await RunAsync("scm", "use", "bot", "--auto", "--project", Repository)).Code.Should().Be(2);
    }

    [Fact]
    public async Task DoctorTellsHowToChooseAmongCandidatesAndToFixAStaleChoice()
    {
        Repo("git@gitlab.example.com:group/repo.git");
        await Store.AddAsync(new("work", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        await Store.AddAsync(new("bot", "gitlab", "https://gitlab.example.com", Secret, null), Token);
        var resolver = new ScmProjectResolver(Store, _directory);

        var ambiguous = await ScmDoctor.ProjectCheckAsync(
            await resolver.ResolveAsync(Repository, null, Token),
            [],
            null,
            Token
        );
        ambiguous.Level.Should().Be("warn");
        ambiguous.Fix.Should().Contain("harness scm use <name>").And.Contain("work, bot");

        await Store.SetChoiceAsync(Path.Combine(Repository, ".git"), "bot", Token);
        await Store.UpdateAsync("bot", new ScmEntryChange(Address: "https://other.example.com"), Token);
        var stale = await ScmDoctor.ProjectCheckAsync(
            await resolver.ResolveAsync(Repository, null, Token),
            [],
            null,
            Token
        );
        stale.Level.Should().Be("warn");
        stale.Detail.Should().Contain("does not serve");
        stale.Fix.Should().Contain("harness scm use --auto");
    }

    private void Repo(string remote)
    {
        var git = Path.Combine(Repository, ".git");
        Directory.CreateDirectory(Path.Combine(git, "refs", "heads"));
        Directory.CreateDirectory(Path.Combine(git, "objects"));
        Directory.CreateDirectory(Path.Combine(Repository, "src"));
        File.WriteAllText(Path.Combine(git, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(
            Path.Combine(git, "config"),
            $"[core]\n\tbare = false\n[remote \"origin\"]\n\turl = {remote}\n"
        );
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(params string[] args)
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
                builder.Services.AddSingleton(new ScmHttpOptions());
            },
            null,
            Token
        );
        return (code, output.ToString(), error.ToString());
    }
}
