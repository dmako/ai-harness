using System.Text.RegularExpressions;

namespace Harness.Scm;

/// <summary>Where a configured SCM's token comes from.</summary>
public enum ScmTokenSource
{
    /// <summary>Stored in the machine's SCM file.</summary>
    Stored,

    /// <summary>Read from an environment variable when a call is made.</summary>
    Environment,
}

/// <summary>A configured SCM as it may be shown: the token only masked.</summary>
/// <param name="TokenEnv">The environment variable holding the token, for <see cref="ScmTokenSource.Environment" />.</param>
/// <param name="MaskedToken">The stored token masked, or the environment variable's value masked; empty when unset.</param>
/// <param name="TokenAvailable">Whether a call could authenticate now (a stored token, or the variable is set).</param>
/// <param name="PlainText">A stored token kept unencrypted (written before tokens were encrypted); the next write encrypts it.</param>
/// <param name="TokenError">Why the stored token cannot be used (it cannot be decrypted), with the fix; null when it can.</param>
public sealed record ScmEntry(
    ScmEndpoint Endpoint,
    ScmTokenSource TokenSource,
    string? TokenEnv,
    string MaskedToken,
    bool TokenAvailable,
    bool PlainText = false,
    string? TokenError = null
)
{
    public string Name => Endpoint.Name;
}

/// <summary>A new SCM: every field as typed, validated by the store.</summary>
/// <param name="Address">The server's base URL as typed.</param>
/// <param name="Token">The token to store; leave null when <paramref name="TokenEnv" /> is given.</param>
public sealed record ScmEntryInput(string Name, string Type, string Address, string? Token, string? TokenEnv);

/// <summary>Changes to a configured SCM; null leaves a field as it is.</summary>
/// <param name="Token">A new stored token; replaces <c>token_env</c>.</param>
/// <param name="TokenEnv">A new environment variable; replaces the stored token.</param>
/// <param name="Rename">A new name.</param>
public sealed record ScmEntryChange(
    string? Type = null,
    string? Address = null,
    string? Token = null,
    string? TokenEnv = null,
    string? Rename = null
);

/// <summary>What the machine's SCM file looks like on disk.</summary>
/// <param name="Exists">Whether the file exists.</param>
/// <param name="Error">Why the file cannot be read, or empty.</param>
/// <param name="OwnerOnly">Whether only the current user can read it; null when it does not exist or cannot be told.</param>
/// <param name="KeyPath">The folder holding the keys stored tokens are encrypted with.</param>
/// <param name="KeysExist">Whether that folder exists.</param>
/// <param name="KeysOwnerOnly">Whether only the current user can access it; null when it does not exist or cannot be told.</param>
public sealed record ScmFileState(
    string Path,
    bool Exists,
    string Error,
    bool? OwnerOnly,
    string PermissionDetail,
    string KeyPath = "",
    bool KeysExist = false,
    bool? KeysOwnerOnly = null,
    string KeysPermissionDetail = ""
);

/// <summary>A configuration request the store refused: an invalid field, a duplicate or unknown name, an unreadable file.</summary>
public sealed class ScmConfigurationException : Exception
{
    public ScmConfigurationException()
        : this("SCM configuration failed.") { }

    public ScmConfigurationException(string message)
        : base(message) { }

    public ScmConfigurationException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// The machine's configured SCMs (one global file for every project). Entries come back with their token masked;
/// only <see cref="Resolve" /> hands a token out, for a provider call.
/// </summary>
public interface IScmConfiguration
{
    /// <summary>The file the entries live in.</summary>
    string FilePath { get; }

    /// <summary>The configured SCMs by name; throws <see cref="ScmConfigurationException" /> when the file is unreadable.</summary>
    IReadOnlyList<ScmEntry> List();

    /// <summary>One SCM by name (case-insensitive), or null.</summary>
    ScmEntry? Find(string name);

    Task<ScmEntry> AddAsync(ScmEntryInput input, CancellationToken cancellationToken);

    Task<ScmEntry> UpdateAsync(string name, ScmEntryChange change, CancellationToken cancellationToken);

    /// <summary>Removes an SCM; false when no SCM has that name.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken);

    /// <summary>The SCM's endpoint and token, for a provider call; throws when it is unknown or has no token.</summary>
    ScmConnection Resolve(string name);

    /// <summary>The file's existence, readability and permissions, for doctor.</summary>
    ScmFileState Inspect();
}

/// <summary>Validation shared by every place SCMs are configured.</summary>
public static partial class ScmEntryRules
{
    /// <summary>A name is 1-64 letters, digits, '-', '_' or '.', starting with a letter or digit.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>An environment variable name: letters, digits and '_', not starting with a digit.</summary>
    public static bool IsValidVariable(string? name) => name is not null && VariablePattern.IsMatch(name);

    public static string NameRule =>
        "An SCM name is 1-64 letters, digits, '-', '_' or '.', starting with a letter or digit.";

    public static ScmKind Kind(string? type) =>
        ScmKinds.TryParse(type, out var kind)
            ? kind.Value
            : throw new ScmConfigurationException(
                $"Unknown SCM type '{type}'; use one of: {string.Join(", ", ScmKinds.Names)}."
            );

    public static Uri BaseUrl(string? address) =>
        ScmEndpoint.TryNormalizeUrl(address, out var normalized, out var error)
            ? normalized
            : throw new ScmConfigurationException(error);

    public static string Name(string? name) =>
        IsValidName(name) ? name! : throw new ScmConfigurationException(NameRule + $" Got '{name}'.");

    public static string Variable(string? name) =>
        IsValidVariable(name)
            ? name!
            : throw new ScmConfigurationException(
                $"'{name}' is not an environment variable name (letters, digits and '_', not starting with a digit)."
            );

    public static string Token(string? token)
    {
        var trimmed = token?.Trim() ?? "";
        if (trimmed.Length is 0)
        {
            throw new ScmConfigurationException("The token is empty.");
        }

        if (trimmed.Any(char.IsWhiteSpace))
        {
            throw new ScmConfigurationException("The token contains whitespace.");
        }

        return trimmed;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern { get; }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern { get; }
}
