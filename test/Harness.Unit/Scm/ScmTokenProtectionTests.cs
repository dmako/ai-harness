using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AwesomeAssertions;
using Harness.Infrastructure.Scm;
using Harness.Scm;
using Xunit;

namespace Harness.Unit.Scm;

public sealed class ScmTokenProtectionTests : IDisposable
{
    private const string Secret = "glpat-AbCdEf0123456789xyz";
    private const string Other = "ghp_OtherSecret0123456789abcdef";
    private readonly string _directory = Directory.CreateTempSubdirectory("harness-scm-keys-").FullName;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string FilePath => Path.Combine(_directory, "scm.toml");

    private string KeyPath => Path.Combine(_directory, ScmTokenProtector.FolderName);

    private ScmConfigurationFile Store() => new(FilePath, _ => null);

    private string[] Keys() => Directory.Exists(KeyPath) ? Directory.GetFiles(KeyPath, "key-*.xml") : [];

    public void Dispose() => Directory.Delete(_directory, true);

    private Task WritePlainAsync() =>
        File.WriteAllTextAsync(
            FilePath,
            $"""
            [scm.a]
            type = "gitlab"
            url = "https://a.example.com"
            token = "{Secret}"

            [scm.b]
            type = "github"
            url = "https://github.com"
            token = "{Other}"
            """,
            Token
        );

    [Fact]
    public async Task StoredTokensAreEncryptedWithKeysBesideTheFileAndRoundTrip()
    {
        var added = await Store()
            .AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);

        added.PlainText.Should().BeFalse();
        added.MaskedToken.Should().Be("****9xyz");
        var text = await File.ReadAllTextAsync(FilePath, Token);
        text.Should().Contain("token_protected = \"").And.NotContain(Secret);
        Keys().Should().ContainSingle();
        Store().Resolve("a").Token.Should().Be(Secret);
        Store().List().Single().Should().BeEquivalentTo(added);
        Store().Protector.KeyDirectory.Should().Be(KeyPath);
    }

    [Fact]
    public async Task PlainTokensAreStillReadAndEncryptedByTheNextWrite()
    {
        await WritePlainAsync();

        var before = Store().List();
        before.Should().AllSatisfy(entry => entry.PlainText.Should().BeTrue());
        Store().Resolve("b").Token.Should().Be(Other);
        Directory.Exists(KeyPath).Should().BeFalse("reading creates no key folder");

        // a change to one SCM re-saves every stored token encrypted
        await Store().UpdateAsync("a", new ScmEntryChange(Type: "gitlab"), Token);

        var text = await File.ReadAllTextAsync(FilePath, Token);
        text.Should().NotContain(Secret).And.NotContain(Other).And.NotContain("token = ");
        text.Split("token_protected = ").Should().HaveCount(3);
        Store().List().Should().AllSatisfy(entry => entry.PlainText.Should().BeFalse());
        Store().Resolve("a").Token.Should().Be(Secret);
        Store().Resolve("b").Token.Should().Be(Other);
    }

    [Fact]
    public async Task ReadingNeverCreatesKeysEvenFromManyProcessesAtOnce()
    {
        await Store().AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);
        var key = Keys().Single();

        await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ =>
                    Task.Run(
                        () =>
                        {
                            Store().Resolve("a").Token.Should().Be(Secret);
                            Store().List().Should().ContainSingle();
                        },
                        Token
                    )
                )
        );

        Keys().Should().Equal(key);

        // a missing key folder is not created by a read either
        Directory.Delete(KeyPath, true);
        Store().List().Single().TokenAvailable.Should().BeFalse();
        Directory.Exists(KeyPath).Should().BeFalse();
    }

    [Fact]
    public async Task TokensThatCannotBeDecryptedNameTheScmAndTheFix()
    {
        await Store().AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);

        // another machine's (or user's) key folder: the key the token names is not in it
        var foreign = Path.Combine(_directory, "foreign");
        Directory.CreateDirectory(foreign);
        var elsewhere = new ScmConfigurationFile(Path.Combine(foreign, "scm.toml"), _ => null);
        await elsewhere.AddAsync(new ScmEntryInput("x", "gitlab", "https://x.example.com", Other, null), Token);
        Directory.Delete(KeyPath, true);
        Directory.Move(Path.Combine(foreign, ScmTokenProtector.FolderName), KeyPath);

        var resolve = () => Store().Resolve("a");
        var failure = resolve.Should().Throw<ScmConfigurationException>().Which;
        failure.Message.Should().Contain("'a'").And.Contain("harness scm set a --token-stdin").And.NotContain(Secret);
        var listed = Store().List().Single();
        listed.TokenAvailable.Should().BeFalse();
        listed.TokenError.Should().Contain("cannot be decrypted");

        Directory.Delete(KeyPath, true);
        resolve.Should().Throw<ScmConfigurationException>().WithMessage("*key folder*missing*harness scm set a*");

        // storing the token again recovers
        await Store().UpdateAsync("a", new ScmEntryChange(Token: Secret), Token);
        Store().Resolve("a").Token.Should().Be(Secret);
    }

    [Fact]
    public async Task TheKeyFolderIsOwnerOnlyAndAWiderOneIsReportedThenTightened()
    {
        await Store().AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);

        var state = Store().Inspect();
        state.KeyPath.Should().Be(KeyPath);
        state.KeysExist.Should().BeTrue();
        state.KeysOwnerOnly.Should().BeTrue(state.KeysPermissionDetail);
        var key = Keys().Single();
        if (OperatingSystem.IsWindows())
        {
            OtherAccounts(new FileInfo(key).GetAccessControl()).Should().BeEmpty();
            AddEveryone(KeyPath);
        }
        else
        {
            File.GetUnixFileMode(KeyPath)
                .Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.GetUnixFileMode(key).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(
                KeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead
            );
        }

        var wide = Store().Inspect();
        wide.KeysOwnerOnly.Should().BeFalse();
        wide.KeysPermissionDetail.Should().NotBeEmpty();

        await Store().UpdateAsync("a", new ScmEntryChange(Type: "gitlab"), Token);
        Store().Inspect().KeysOwnerOnly.Should().BeTrue();
    }

    [Fact]
    public async Task OnWindowsTheKeysAreThemselvesProtectedWithDpapi()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI key protection exists on Windows only.");
        await Store().AddAsync(new ScmEntryInput("a", "gitlab", "https://a.example.com", Secret, null), Token);

        var xml = await File.ReadAllTextAsync(Keys().Single(), Token);

        xml.Should().Contain("<encryptedKey").And.Contain("DpapiXmlDecryptor").And.NotContain("unencrypted form");
    }

    [SupportedOSPlatform("windows")]
    private static List<string> OtherAccounts(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return security
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType is AccessControlType.Allow && rule.IdentityReference != identity.User)
            .Select(rule => rule.IdentityReference.Value)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static void AddEveryone(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(
            new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow
            )
        );
        info.SetAccessControl(security);
    }
}
