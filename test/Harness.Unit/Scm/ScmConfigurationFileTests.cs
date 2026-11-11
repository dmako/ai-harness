using AwesomeAssertions;
using Harness.Infrastructure.Scm;
using Harness.Scm;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmConfigurationFileTests : IDisposable
{
    private const string Secret = "glpat-AbCdEf0123456789xyz";
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-").FullName;
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string FilePath => Path.Combine(_directory, "scm.toml");

    private ScmConfigurationFile Store() => new(FilePath, name => _environment.GetValueOrDefault(name));

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task EntriesPersistInScmTomlBesideTheGlobalConfigurationWithTheirTokenMasked()
    {
        ScmConfigurationFile.ForGlobal(Path.Combine(_directory, "config.toml")).FilePath.Should().Be(FilePath);

        var added = await Store()
            .AddAsync(new ScmEntryInput("work", "GitLab", "https://gitlab.example.com/", Secret, null), Token);

        added.Endpoint.Kind.Should().Be(ScmKind.Gitlab);
        added.Endpoint.BaseUrl.AbsoluteUri.Should().Be("https://gitlab.example.com/");
        added.MaskedToken.Should().Be("****9xyz");
        added.TokenSource.Should().Be(ScmTokenSource.Stored);
        added.TokenAvailable.Should().BeTrue();

        var text = await File.ReadAllTextAsync(FilePath, Token);
        text.Should()
            .Contain("[scm.work]")
            .And.Contain("type = \"gitlab\"")
            .And.Contain("url = \"https://gitlab.example.com\"");
        text.Should().Contain($"token = \"{Secret}\"");

        var fresh = Store();
        fresh.List().Should().ContainSingle().Which.Should().BeEquivalentTo(added);
        fresh.Find("WORK")!.Name.Should().Be("work");
        fresh.Find("other").Should().BeNull();
        fresh.List().Select(entry => entry.ToString()).Should().NotContain(line => line.Contains(Secret));
        fresh.Resolve("work").Token.Should().Be(Secret);
        fresh.Resolve("work").ToString().Should().NotContain(Secret);
    }

    [Fact]
    public async Task MissingFileMeansNoScmAndNoFileIsCreatedByReads()
    {
        Store().List().Should().BeEmpty();
        Store().Inspect().Exists.Should().BeFalse();
        File.Exists(FilePath).Should().BeFalse();
        (await Store().RemoveAsync("none", Token)).Should().BeFalse();
    }

    [Theory]
    [InlineData("-bad", "gitlab", "https://h", Secret, null, "name")]
    [InlineData("has space", "gitlab", "https://h", Secret, null, "name")]
    [InlineData("ok", "github", "https://h", Secret, null, "Unknown SCM type 'github'")]
    [InlineData("ok", "gitlab", "gitlab.example.com", Secret, null, "http")]
    [InlineData("ok", "gitlab", "https://h?x=1", Secret, null, "base address")]
    [InlineData("ok", "gitlab", "https://h", null, null, "token is required")]
    [InlineData("ok", "gitlab", "https://h", Secret, "GL_TOKEN", "not both")]
    [InlineData("ok", "gitlab", "https://h", null, "1BAD", "environment variable name")]
    [InlineData("ok", "gitlab", "https://h", "two words", null, "whitespace")]
    public async Task InvalidEntriesAreRefusedWithTheReasonAndNothingIsWritten(
        string name,
        string type,
        string address,
        string? token,
        string? variable,
        string reason
    )
    {
        var add = () => Store().AddAsync(new ScmEntryInput(name, type, address, token, variable), Token);

        var failure = await add.Should().ThrowAsync<ScmConfigurationException>();
        failure.Which.Message.Should().Contain(reason).And.NotContain(Secret);
        File.Exists(FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task NamesAreUniqueIgnoringCase()
    {
        await Store().AddAsync(new ScmEntryInput("Forge", "forgejo", "https://git.example.org", Secret, null), Token);

        var duplicate = () =>
            Store().AddAsync(new ScmEntryInput("forge", "gitlab", "https://gitlab.com", Secret, null), Token);

        (await duplicate.Should().ThrowAsync<ScmConfigurationException>())
            .Which.Message.Should()
            .Contain("already exists");
        Store().List().Should().ContainSingle();
    }

    [Fact]
    public async Task UpdatesRenameRetargetAndSwitchBetweenStoredAndEnvironmentTokens()
    {
        var store = Store();
        await store.AddAsync(new ScmEntryInput("a", "gitlab", "https://one.example.com", Secret, null), Token);
        await store.AddAsync(new ScmEntryInput("b", "forgejo", "https://two.example.com", Secret, null), Token);

        var renamed = await store.UpdateAsync(
            "A",
            new ScmEntryChange(Address: "https://one.example.com/gitlab/", Rename: "alpha"),
            Token
        );
        renamed.Name.Should().Be("alpha");
        renamed.Endpoint.BaseUrl.AbsoluteUri.Should().Be("https://one.example.com/gitlab");

        var toB = () => store.UpdateAsync("alpha", new ScmEntryChange(Rename: "B"), Token);
        (await toB.Should().ThrowAsync<ScmConfigurationException>()).Which.Message.Should().Contain("already exists");

        var env = await store.UpdateAsync("alpha", new ScmEntryChange(TokenEnv: "GL_TOKEN"), Token);
        env.TokenSource.Should().Be(ScmTokenSource.Environment);
        env.TokenAvailable.Should().BeFalse();
        env.MaskedToken.Should().BeEmpty();
        (await File.ReadAllTextAsync(FilePath, Token)).Should().Contain("token_env = \"GL_TOKEN\"");
        var resolve = () => store.Resolve("alpha");
        resolve.Should().Throw<ScmConfigurationException>().WithMessage("*GL_TOKEN*not set*");

        _environment["GL_TOKEN"] = "env-token-0123456789";
        store.Find("alpha")!.MaskedToken.Should().Be("****6789");
        store.Resolve("alpha").Token.Should().Be("env-token-0123456789");

        var stored = await store.UpdateAsync(
            "alpha",
            new ScmEntryChange(Type: "forgejo", Token: "new-token-abcdefgh"),
            Token
        );
        stored.TokenSource.Should().Be(ScmTokenSource.Stored);
        stored.Endpoint.Kind.Should().Be(ScmKind.Forgejo);
        (await File.ReadAllTextAsync(FilePath, Token))
            .Should()
            .NotContain("token_env")
            .And.Contain("new-token-abcdefgh");

        Store().List().Select(entry => entry.Name).Should().Equal("alpha", "b");
        var missing = () => store.UpdateAsync("zeta", new ScmEntryChange(Type: "gitlab"), Token);
        await missing.Should().ThrowAsync<ScmConfigurationException>().WithMessage("No SCM is named 'zeta'.");
    }

    [Fact]
    public async Task RemovingKeepsTheOtherEntries()
    {
        var store = Store();
        await store.AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);
        await store.AddAsync(new ScmEntryInput("b.dotted", "gitlab", "https://b.example.com", Secret, null), Token);

        (await store.RemoveAsync("A", Token)).Should().BeTrue();
        (await store.RemoveAsync("a", Token)).Should().BeFalse();

        Store().List().Should().ContainSingle().Which.Name.Should().Be("b.dotted");
        (await File.ReadAllTextAsync(FilePath, Token)).Should().Contain("[scm.\"b.dotted\"]");
    }

    [Fact]
    public async Task TokensWithQuotesAndBackslashesRoundTrip()
    {
        const string Odd = "to\"ken\\with'quotes";
        await Store().AddAsync(new ScmEntryInput("x", "forgejo", "http://localhost:3000", Odd, null), Token);

        Store().Resolve("x").Token.Should().Be(Odd);
    }

    [Fact]
    public async Task MalformedFilesAreReportedWithoutEchoingTokens()
    {
        await File.WriteAllTextAsync(FilePath, $"[scm.a]\ntype = \"gitlab\"\ntoken = \"{Secret}\"\n", Token);
        var missingUrl = () => Store().List();
        missingUrl
            .Should()
            .Throw<ScmConfigurationException>()
            .Which.Message.Should()
            .Contain("'url' is missing")
            .And.NotContain(Secret);

        await File.WriteAllTextAsync(FilePath, $"[scm.a\ntoken = \"{Secret}\"\n", Token);
        var broken = Store().Inspect();
        broken.Exists.Should().BeTrue();
        broken.Error.Should().Contain("not valid TOML").And.NotContain(Secret);

        await File.WriteAllTextAsync(FilePath, "[scm.a]\ntype = \"gitlab\"\nurl = 3\n", Token);
        Store().Inspect().Error.Should().Contain("'url' must be a string");
    }

    [Fact]
    public async Task WrittenFilesAreOwnerOnlyAndWiderPermissionsAreReportedThenTightened()
    {
        await Store().AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);
        var written = Store().Inspect();
        written.OwnerOnly.Should().BeTrue(written.PermissionDetail);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(FilePath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // a file created by hand: inherited ACL on Windows, the umask's mode elsewhere
        var text = await File.ReadAllTextAsync(FilePath, Token);
        File.Delete(FilePath);
        await File.WriteAllTextAsync(FilePath, text, Token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }

        var wide = Store().Inspect();
        wide.OwnerOnly.Should().BeFalse();
        wide.PermissionDetail.Should().NotBeEmpty();

        await Store().UpdateAsync("a", new ScmEntryChange(Type: "gitlab"), Token);
        Store().Inspect().OwnerOnly.Should().BeTrue();
        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }
}
