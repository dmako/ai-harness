using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Scm;

/// <summary>
/// Another name under which an SCM's server is reached, used only to match git remotes to it: a host name alone
/// (<c>git-alias</c>, which also covers an SSH host alias from ~/.ssh/config), or a base URL
/// (<c>https://git-alias.example.com[/prefix]</c>, <c>ssh://git@ssh-alias[:port][/prefix]</c>). A URL alias's path
/// prefix stands for the SCM URL's own base path when the project path is worked out; API calls always go to the
/// SCM's URL.
/// </summary>
/// <param name="Text">The alias as stored and shown: a lower-case host, or the normalised base URL.</param>
/// <param name="Scheme">http, https or ssh; null for a host name alone.</param>
/// <param name="Host">The host name, lower case.</param>
/// <param name="Port">The explicit port, null when the URL names none (or its scheme's default).</param>
/// <param name="Path">The base path's segments; empty for a host name alone.</param>
[SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "Host names and URL schemes are conventionally shown and compared in lower case."
)]
public sealed partial record ScmAlias(string Text, string? Scheme, string Host, int? Port, IReadOnlyList<string> Path)
{
    /// <summary>Whether the alias is a host name alone, matching that host over any protocol and port.</summary>
    public bool IsHost => Scheme is null;

    /// <summary>Whether the alias is a web address (http or https).</summary>
    public bool IsHttp => Scheme is "http" or "https";

    /// <summary>Whether two aliases name the same thing (case-insensitive).</summary>
    public bool Same(ScmAlias other) =>
        other is not null && string.Equals(Text, other.Text, StringComparison.OrdinalIgnoreCase);

    public bool Equals(ScmAlias? other) => other is not null && Same(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Text);

    public override string ToString() => Text;

    /// <summary>Parses and normalises a typed alias; <paramref name="error" /> says what is wrong when it fails.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ScmAlias? alias, out string error)
    {
        alias = null;
        var value = text?.Trim() ?? "";
        if (value.Length is 0)
        {
            error = "An alias is empty.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            if (!HostPattern.IsMatch(value))
            {
                error =
                    $"'{value}' is not an alias: give a host name (git-alias.example.com, an SSH host alias) or a "
                    + "base URL (https://git-alias.example.com, ssh://git@ssh-alias).";
                return false;
            }

            var host = value.ToLowerInvariant();
            alias = new ScmAlias(host, null, host, null, []);
            error = "";
            return true;
        }

        return TryParseUrl(value, out alias, out error);
    }

    private static bool TryParseUrl(string value, [NotNullWhen(true)] out ScmAlias? alias, out string error)
    {
        alias = null;
        error =
            $"'{Safe(value)}' is not an alias URL: give an http://, https:// or ssh:// base address without query or "
            + "fragment, e.g. https://git-alias.example.com or ssh://git@ssh-alias:2222.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length is 0)
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => "http",
            "https" => "https",
            "ssh" or "git+ssh" or "ssh+git" => "ssh",
            _ => null,
        };
        if (scheme is null || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (scheme is not "ssh" && uri.UserInfo.Length > 0)
        {
            // a web address's user part may carry a password or token: never store it
            error = "An alias URL must not contain a user name or password.";
            return false;
        }

        var host = uri.Host.ToLowerInvariant();
        int? port = uri.Port switch
        {
            < 0 => null,
            22 when scheme is "ssh" => null,
            _ when uri.IsDefaultPort => null,
            var explicitPort => explicitPort,
        };
        var path = uri
            .AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        var text =
            $"{scheme}://{host}{(port is null ? "" : ":" + port)}"
            + (path.Length is 0 ? "" : "/" + string.Join('/', path.Select(Uri.EscapeDataString)));
        alias = new ScmAlias(text, scheme, host, port, path);
        error = "";
        return true;
    }

    /// <summary>The typed text without anything that may be a password, for messages.</summary>
    private static string Safe(string value) =>
        value.Contains('@', StringComparison.Ordinal) && !value.StartsWith("ssh", StringComparison.OrdinalIgnoreCase)
            ? ScmRemoteUrls.Safe(value)
            : value;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,252})$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern { get; }
}
