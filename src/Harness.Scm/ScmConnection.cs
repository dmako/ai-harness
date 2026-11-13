namespace Harness.Scm;

/// <summary>A configured SCM without its secret: what may be shown, logged and matched.</summary>
/// <param name="Name">The user's name for the connection, unique on the machine.</param>
/// <param name="BaseUrl">The server's base address (http or https), without a trailing slash.</param>
public sealed record ScmEndpoint(string Name, ScmKind Kind, Uri BaseUrl)
{
    /// <summary>The base URL's path segments, e.g. ["gitlab"] for https://host/gitlab.</summary>
    public IReadOnlyList<string> BasePath { get; } =
        BaseUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

    /// <summary>
    /// Other host names or base URLs under which the server is reached, used only to match remotes (never for API
    /// calls); see <see cref="ScmAlias" />.
    /// </summary>
    public IReadOnlyList<ScmAlias> Aliases { get; init; } = [];

    public bool Equals(ScmEndpoint? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Kind == other.Kind
        && BaseUrl == other.BaseUrl
        && Aliases.SequenceEqual(other.Aliases);

    public override int GetHashCode() => HashCode.Combine(Name, Kind, BaseUrl);

    /// <summary>Normalises a typed base URL: absolute http(s), no query, fragment or trailing slash.</summary>
    public static bool TryNormalizeUrl(string? text, out Uri url, out string error)
    {
        url = null!;
        if (
            !Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
        )
        {
            error = "The URL must be an absolute http:// or https:// address, e.g. https://gitlab.example.com.";
            return false;
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || parsed.UserInfo.Length > 0)
        {
            error = "The URL must be the server's base address, without user name, query or fragment.";
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(parsed) { Path = path.Length == 0 ? "/" : path };
        url = new Uri(builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
        error = "";
        return true;
    }
}

/// <summary>An endpoint with the token that authenticates calls to it. Its text form never shows the token.</summary>
public sealed record ScmConnection(ScmEndpoint Endpoint, string Token)
{
    /// <summary>The URL a relative API path resolves against: the base URL with a trailing slash.</summary>
    public Uri ApiRoot { get; } = new(Endpoint.BaseUrl.AbsoluteUri.TrimEnd('/') + "/");

    public override string ToString() => $"ScmConnection {{ Endpoint = {Endpoint}, Token = {ScmTokens.Mask(Token)} }}";
}

/// <summary>Shows tokens only masked.</summary>
public static class ScmTokens
{
    /// <summary>A token's masked form: its last four characters at most, e.g. "****abcd".</summary>
    public static string Mask(string? token) =>
        token switch
        {
            null or "" => "",
            { Length: <= 8 } => "****",
            _ => "****" + token[^4..],
        };
}
