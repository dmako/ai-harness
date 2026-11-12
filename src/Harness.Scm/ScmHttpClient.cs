using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Harness.Scm;

/// <summary>A successful answer: its JSON value and headers (paging).</summary>
public sealed record ScmResponse<T>(T Value, HttpResponseHeaders Headers);

/// <summary>
/// The HTTP plumbing SCM providers share: relative API paths against the connection's base URL, the provider's
/// authentication header, a per-request timeout, one bounded retry after HTTP 429, paging up to
/// <see cref="ScmLimits.MaxPages" />, source-generated JSON and failures mapped to <see cref="ScmException" /> with the
/// token removed from any server text.
/// </summary>
public sealed class ScmHttpClient : IDisposable
{
    private const int MaxErrorText = 400;
    private readonly HttpClient _http;
    private readonly Action<HttpRequestHeaders> _authenticate;

    /// <param name="authenticate">Adds the token header to each request (PRIVATE-TOKEN, Authorization).</param>
    public ScmHttpClient(
        ScmConnection connection,
        HttpMessageHandler handler,
        ScmLimits limits,
        Action<HttpRequestHeaders> authenticate
    )
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(limits);
        Connection = connection;
        Limits = limits;
        _authenticate = authenticate;
        _http = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = connection.ApiRoot,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("harness", "1"));
    }

    public ScmConnection Connection { get; }

    public ScmLimits Limits { get; }

    /// <summary>One handler for every client in the process, so connections are pooled across calls.</summary>
    public static HttpMessageHandler SharedHandler { get; } =
        new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

    public async Task<ScmResponse<T>> GetAsync<T>(
        string path,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken
    )
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return new ScmResponse<T>(
            await ReadAsync(response, path, type, cancellationToken).ConfigureAwait(false),
            response.Headers
        );
    }

    public async Task<string> GetTextAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> SendAsync<TBody, T>(
        HttpMethod method,
        string path,
        TBody body,
        JsonTypeInfo<TBody> bodyType,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken
    )
    {
        var json = JsonSerializer.Serialize(body, bodyType);
        using var response = await SendAsync(method, path, json, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, path, type, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every page of a list, following pages while a full page came back (and the server's X-Next-Page, when it sends
    /// one, names a next page), at most <see cref="ScmLimits.MaxPages" /> pages.
    /// </summary>
    /// <param name="path">The list's path for a 1-based page number and the page size.</param>
    public Task<List<T>> GetPagesAsync<T>(
        Func<int, int, string> path,
        JsonTypeInfo<List<T>> type,
        CancellationToken cancellationToken
    ) => GetPagesAsync(path, type, page => page, cancellationToken);

    /// <summary>Every page of a list each page wraps in an envelope (Forgejo's <c>{"ok":…,"data":[…]}</c>).</summary>
    /// <param name="items">The page's items inside its envelope.</param>
    public async Task<List<T>> GetPagesAsync<TPage, T>(
        Func<int, int, string> path,
        JsonTypeInfo<TPage> type,
        Func<TPage, IReadOnlyList<T>?> items,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(items);
        List<T> all = [];
        for (var page = 1; page <= Limits.MaxPages; page++)
        {
            var (envelope, headers) = await GetAsync(path(page, Limits.PageSize), type, cancellationToken)
                .ConfigureAwait(false);
            var values = items(envelope);
            all.AddRange(values ?? []);
            var next = headers.TryGetValues("X-Next-Page", out var nextValues) ? nextValues.FirstOrDefault() : null;
            if ((values?.Count ?? 0) < Limits.PageSize || next is { Length: 0 })
            {
                break;
            }
        }

        return all;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken
    )
    {
        var retried = false;
        while (true)
        {
            using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            _authenticate(request.Headers);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var response = await TransmitAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                var wait = RetryWait(response);
                if (response.StatusCode is HttpStatusCode.TooManyRequests && !retried && wait <= Limits.MaxRetryWait)
                {
                    retried = true;
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw await FailureAsync(method, path, response, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> TransmitAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Limits.Timeout);
        try
        {
            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(timeout.Token).ConfigureAwait(false);
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            throw ScmErrors.FromTransport(Connection.Endpoint.BaseUrl, Redacted(exception));
        }
    }

    private static TimeSpan RetryWait(HttpResponseMessage response) =>
        response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => TimeSpan.FromSeconds(1),
        };

    private async Task<ScmException> FailureAsync(
        HttpMethod method,
        string path,
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        var detail = "";
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            detail = ServerMessage(text);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        { /* The status alone describes the failure. */
        }

        var status = (int)response.StatusCode;
        var message =
            $"{method} {PathOnly(path)}: HTTP {status} {response.ReasonPhrase}"
            + (detail.Length > 0 ? ": " + ScmErrors.Redact(detail, Connection.Token) : "");
        return new ScmException(ScmErrors.FromStatus(response.StatusCode), message, response.StatusCode);
    }

    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string path,
        JsonTypeInfo<T> type,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken).ConfigureAwait(false)
                    ?? throw new JsonException("The answer was null.");
            }
        }
        catch (JsonException exception)
        {
            throw new ScmException(
                ScmErrorKind.WrongServer,
                $"GET {PathOnly(path)}: {Connection.Endpoint.BaseUrl.Host} answered something that is not the expected "
                    + $"{ScmKinds.Display(Connection.Endpoint.Kind)} JSON ({response.Content.Headers.ContentType?.MediaType ?? "no content type"}).",
                response.StatusCode,
                exception
            );
        }
    }

    /// <summary>The "message" or "error" of a JSON error body, else its start, on one line.</summary>
    private static string ServerMessage(string text)
    {
        var message = text;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind is JsonValueKind.Object)
            {
                foreach (var key in new[] { "message", "error", "error_description" })
                {
                    if (document.RootElement.TryGetProperty(key, out var value))
                    {
                        message = value.ValueKind is JsonValueKind.String ? value.GetString()! : value.GetRawText();
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            message = text.TrimStart().StartsWith("<", StringComparison.Ordinal) ? "" : text;
        }

        message = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return message.Length > MaxErrorText ? message[..MaxErrorText] + "…" : message;
    }

    private static string PathOnly(string path) => path.Split('?', 2)[0];

    private Exception Redacted(Exception exception) =>
        exception.Message.Contains(Connection.Token, StringComparison.Ordinal)
            ? new HttpRequestException(ScmErrors.Redact(exception.Message, Connection.Token))
            : exception;

    public void Dispose() => _http.Dispose();
}
