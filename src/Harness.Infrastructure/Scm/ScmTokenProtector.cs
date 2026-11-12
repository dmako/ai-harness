using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Harness.Scm;
using Microsoft.AspNetCore.DataProtection;

namespace Harness.Infrastructure.Scm;

/// <summary>
/// Encrypts stored SCM tokens with ASP.NET Core Data Protection: authenticated encryption under a key ring kept in an
/// owner-only folder beside <c>scm.toml</c> (DPAPI-protected on Windows). Only writes, which hold the scm.toml lock,
/// may create or rotate keys; reads never create the folder or a key, so harness processes started side by side do
/// not each add one. A copy of scm.toml without its key folder no longer reveals the tokens; a process running as the
/// same user can still decrypt them, as harness does.
/// </summary>
public sealed class ScmTokenProtector
{
    public const string FolderName = "scm-keys";

    /// <summary>Fixed, so payloads do not depend on where harness is installed.</summary>
    internal const string ApplicationName = "harness";

    internal const string Purpose = "Harness.Scm.Token.v1";

    public ScmTokenProtector(string keyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDirectory);
        KeyDirectory = Path.GetFullPath(keyDirectory);
    }

    public string KeyDirectory { get; }

    /// <summary>The protector beside an SCM file (<c>&lt;dir&gt;/scm-keys</c>).</summary>
    public static ScmTokenProtector ForFile(string scmFilePath) =>
        new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scmFilePath))!, FolderName));

    /// <summary>
    /// Encrypts a token, creating the key folder (owner-only) and a key when needed. Call it only while holding the
    /// scm.toml write lock.
    /// </summary>
    public string Protect(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        EnsureKeyDirectory();
        return Protector(allowKeyGeneration: true).Protect(token);
    }

    /// <summary>Decrypts a stored token; throws <see cref="ScmConfigurationException" /> naming the fix when it cannot.</summary>
    public string Unprotect(string payload, string scmName)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        if (!Directory.Exists(KeyDirectory))
        {
            throw Undecryptable(scmName, $"its key folder {KeyDirectory} is missing");
        }

        try
        {
            return Protector(allowKeyGeneration: false).Unprotect(payload);
        }
        catch (CryptographicException exception)
        {
            throw Undecryptable(scmName, FirstSentence(exception.Message), exception);
        }
    }

    /// <summary>Whether the key folder exists and only the current user may access it, for doctor.</summary>
    public (bool Exists, bool? OwnerOnly, string Detail) Inspect()
    {
        if (!Directory.Exists(KeyDirectory))
        {
            return (false, null, "");
        }

        var (ownerOnly, detail) = ScmConfigurationFile.Permissions(KeyDirectory, directory: true);
        return (true, ownerOnly, detail);
    }

    private IDataProtector Protector(bool allowKeyGeneration) =>
        DataProtectionProvider
            .Create(
                new DirectoryInfo(KeyDirectory),
                builder =>
                {
                    builder.SetApplicationName(ApplicationName);
                    if (!allowKeyGeneration)
                    {
                        builder.DisableAutomaticKeyGeneration();
                    }

                    if (OperatingSystem.IsWindows())
                    {
                        builder.ProtectKeysWithDpapi();
                    }
                }
            )
            .CreateProtector(Purpose);

    /// <summary>Creates the key folder, or tightens an existing one, so only the current user may access it.</summary>
    public void EnsureKeyDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(KeyDirectory);
            var security = OwnerOnlyDirectory();
            if (directory.Exists)
            {
                directory.SetAccessControl(security);
            }
            else
            {
                directory.Create(security);
            }

            return;
        }

        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(KeyDirectory, OwnerOnly);
        File.SetUnixFileMode(KeyDirectory, OwnerOnly);
    }

    /// <summary>Full control for the current user only, inherited by the key files, nothing inherited from above.</summary>
    [SupportedOSPlatform("windows")]
    private static DirectorySecurity OwnerOnlyDirectory()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(identity.User!);
        security.AddAccessRule(
            new FileSystemAccessRule(
                identity.User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow
            )
        );
        return security;
    }

    private ScmConfigurationException Undecryptable(string scmName, string reason, Exception? inner = null)
    {
        var message =
            $"The stored token of SCM '{scmName}' cannot be decrypted ({reason}): the key folder {KeyDirectory} "
            + "must be the one it was saved with, for the same user account. "
            + $"Store the token again with 'harness scm set {scmName} --token-stdin'.";
        return inner is null ? new ScmConfigurationException(message) : new ScmConfigurationException(message, inner);
    }

    /// <summary>The exception's first sentence, without Data Protection's link to its documentation.</summary>
    private static string FirstSentence(string message)
    {
        var end = message.IndexOf(". ", StringComparison.Ordinal);
        return (end > 0 ? message[..end] : message.TrimEnd('.')).Trim();
    }
}
