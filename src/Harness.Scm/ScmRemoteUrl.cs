using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Scm;

/// <summary>
/// A git remote URL split into what matching needs: https://host/group/repo.git, ssh://git@host:2222/group/repo,
/// git://host/group/repo and the scp-like git@host:group/repo.git all become a host, an optional port and the path's
/// segments without the ".git" suffix.
/// </summary>
/// <param name="Scheme">http, https, ssh or git; scp-like remotes are ssh.</param>
/// <param name="Host">The host name, lower case.</param>
/// <param name="Port">The explicit port, null when the URL names none.</param>
[SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "Host names and URL schemes are conventionally shown and compared in lower case."
)]
public sealed partial record ScmRemoteUrl(string Scheme, string Host, int? Port, IReadOnlyList<string> Path)
{
    /// <summary>Whether the remote uses a web protocol, whose host and port are the SCM's own.</summary>
    public bool IsHttp => Scheme is "http" or "https";

    /// <summary>The port the remote connects to: its explicit port or its scheme's default.</summary>
    public int EffectivePort =>
        Port
        ?? Scheme switch
        {
            "https" => 443,
            "http" => 80,
            "git" => 9418,
            _ => 22,
        };

    public static bool TryParse(string? text, [NotNullWhen(true)] out ScmRemoteUrl? remote)
    {
        remote = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return TryParseUrl(value, out remote);
        }

        var match = ScpLike.Match(value);
        if (!match.Success || match.Groups["host"].Value.Length < 2)
        {
            // a single letter before ':' is a Windows drive (C:\repo), not a host
            return false;
        }

        remote = new ScmRemoteUrl(
            "ssh",
            match.Groups["host"].Value.ToLowerInvariant(),
            null,
            Segments(match.Groups["path"].Value)
        );
        return remote.Path.Count > 0;
    }

    private static bool TryParseUrl(string value, [NotNullWhen(true)] out ScmRemoteUrl? remote)
    {
        remote = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant() switch
        {
            "http" => "http",
            "https" => "https",
            "ssh" or "git+ssh" or "ssh+git" => "ssh",
            "git" => "git",
            _ => null,
        };
        if (scheme is null)
        {
            return false;
        }

        int? port = uri.IsDefaultPort || uri.Port < 0 ? null : uri.Port;
        remote = new ScmRemoteUrl(scheme, uri.Host.ToLowerInvariant(), port, Segments(uri.AbsolutePath));
        return remote.Path.Count > 0;
    }

    private static string[] Segments(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segments.Count > 0 && segments[0] == "~")
        {
            segments.RemoveAt(0);
        }

        if (segments.Count > 0 && segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            segments[^1] = segments[^1][..^4];
            if (segments[^1].Length == 0)
            {
                segments.RemoveAt(segments.Count - 1);
            }
        }

        return [.. segments];
    }

    public override string ToString() =>
        $"{Scheme}://{Host}{(Port is null ? "" : ":" + Port)}/{string.Join('/', Path)}";

    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[^:/\s\\]+):(?<path>[^\\].*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScpLike { get; }
}
