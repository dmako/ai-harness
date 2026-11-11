using System.Net;

namespace Harness.Scm;

/// <summary>Why an SCM call failed, in terms a user or agent can act on.</summary>
public enum ScmErrorKind
{
    /// <summary>The server could not be reached (DNS, refused connection, timeout).</summary>
    Unreachable,

    /// <summary>The TLS handshake or certificate check failed.</summary>
    Tls,

    /// <summary>The token was rejected (HTTP 401).</summary>
    Unauthorized,

    /// <summary>The token lacks the permission or scope for this call (HTTP 403).</summary>
    Forbidden,

    /// <summary>The project, merge request or other object does not exist or is not visible to the token (HTTP 404).</summary>
    NotFound,

    /// <summary>The server is not the configured kind of SCM (a GitLab URL configured as Forgejo, a web page, a proxy).</summary>
    WrongServer,

    /// <summary>The server refused the request's content (HTTP 400/409/422), e.g. a diff line that is not in the diff.</summary>
    Invalid,

    /// <summary>The server asked to slow down (HTTP 429) for longer than harness waits.</summary>
    RateLimited,

    /// <summary>The server failed (HTTP 5xx) or answered something harness could not read.</summary>
    Server,
}

/// <summary>A failed SCM call. Its message never contains the access token.</summary>
public sealed class ScmException : Exception
{
    public ScmException(ScmErrorKind kind, string message, HttpStatusCode? status = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Status = status;
    }

    public ScmException()
        : this(ScmErrorKind.Server, "SCM call failed.") { }

    public ScmException(string message)
        : this(ScmErrorKind.Server, message) { }

    public ScmException(string message, Exception inner)
        : this(ScmErrorKind.Server, message, null, inner) { }

    public ScmErrorKind Kind { get; }

    public HttpStatusCode? Status { get; }
}

/// <summary>Names and fix hints for <see cref="ScmErrorKind" />, and classification of HTTP failures.</summary>
public static class ScmErrors
{
    public static string Name(ScmErrorKind kind) =>
        kind switch
        {
            ScmErrorKind.Unreachable => "unreachable",
            ScmErrorKind.Tls => "tls",
            ScmErrorKind.Unauthorized => "unauthorized",
            ScmErrorKind.Forbidden => "forbidden",
            ScmErrorKind.NotFound => "not_found",
            ScmErrorKind.WrongServer => "wrong_server",
            ScmErrorKind.Invalid => "invalid",
            ScmErrorKind.RateLimited => "rate_limited",
            ScmErrorKind.Server => "server",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SCM error kind."),
        };

    public static string Hint(ScmErrorKind kind) =>
        kind switch
        {
            ScmErrorKind.Unreachable => "Check the URL, your network or VPN and any proxy; the server did not answer.",
            ScmErrorKind.Tls =>
                "The server's certificate was not accepted: check the URL's host name and that the certificate's issuer is trusted by this machine.",
            ScmErrorKind.Unauthorized =>
                "The token was rejected: it may be wrong, expired or revoked. Create a new token and update it with 'harness scm set <name> --token-stdin'.",
            ScmErrorKind.Forbidden =>
                "The token lacks permission: give it the 'api' scope (GitLab) or repository and issue write access (Forgejo), or use an account with access to the project.",
            ScmErrorKind.NotFound =>
                "Not found: check the project path and number, and that the token's user can see the project.",
            ScmErrorKind.WrongServer =>
                "The URL does not answer like the configured SCM type: check the type (gitlab or forgejo) and that the URL is the server's base address.",
            ScmErrorKind.Invalid =>
                "The server refused the request's content; check the arguments (for a diff comment, the line must be part of the merge request's diff).",
            ScmErrorKind.RateLimited => "The server is rate limiting requests; try again later.",
            ScmErrorKind.Server =>
                "The server failed or answered unexpectedly; try again later or check the server's status.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SCM error kind."),
        };

    /// <summary>The error kind an unsuccessful HTTP status means.</summary>
    public static ScmErrorKind FromStatus(HttpStatusCode status) =>
        status switch
        {
            HttpStatusCode.Unauthorized => ScmErrorKind.Unauthorized,
            HttpStatusCode.Forbidden => ScmErrorKind.Forbidden,
            HttpStatusCode.NotFound => ScmErrorKind.NotFound,
            HttpStatusCode.TooManyRequests => ScmErrorKind.RateLimited,
            HttpStatusCode.BadRequest
            or HttpStatusCode.Conflict
            or HttpStatusCode.UnprocessableEntity
            or HttpStatusCode.MethodNotAllowed => ScmErrorKind.Invalid,
            _ => ScmErrorKind.Server,
        };

    /// <summary>
    /// The SCM error for a transport failure: TLS when the handshake failed, unreachable otherwise. The message names
    /// the host only, never the request's headers.
    /// </summary>
    public static ScmException FromTransport(Uri server, Exception exception) =>
        exception switch
        {
            HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => new ScmException(
                ScmErrorKind.Tls,
                $"TLS connection to {server.Host} failed: {exception.Message}",
                inner: exception
            ),
            TaskCanceledException or TimeoutException => new ScmException(
                ScmErrorKind.Unreachable,
                $"{server.Host} did not answer in time.",
                inner: exception
            ),
            _ => new ScmException(
                ScmErrorKind.Unreachable,
                $"Cannot reach {server.Host}: {exception.Message}",
                inner: exception
            ),
        };

    /// <summary>Removes every occurrence of <paramref name="token" /> from text a server or library produced.</summary>
    public static string Redact(string text, string? token) =>
        string.IsNullOrEmpty(token) || token.Length < 4 ? text : text.Replace(token, "***", StringComparison.Ordinal);
}
