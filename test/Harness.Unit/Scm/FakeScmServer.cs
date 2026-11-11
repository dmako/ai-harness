using System.Net;
using System.Text;

namespace Harness.Unit.Scm;

/// <summary>A request the fake server received.</summary>
internal sealed record FakeRequest(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)
{
    public string Path => Uri.AbsolutePath;

    public string Query => Uri.Query;
}

/// <summary>A canned answer.</summary>
internal sealed record FakeResponse(
    HttpStatusCode Status,
    string Body,
    string ContentType = "application/json",
    Dictionary<string, string>? Headers = null,
    TimeSpan Delay = default
);

/// <summary>
/// An in-memory SCM server: answers are queued per "METHOD /path" (query ignored), the last one repeating; every
/// request is recorded with its headers and body. Unknown routes answer 404 with a GitLab-style message.
/// </summary>
internal sealed class FakeScmServer : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<FakeResponse>> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeResponse> _last = new(StringComparer.Ordinal);

    public List<FakeRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, Exception?>? Fail { get; set; }

    public FakeScmServer On(string method, string path, params FakeResponse[] responses)
    {
        var key = method + " " + path;
        if (!_routes.TryGetValue(key, out var queue))
        {
            _routes[key] = queue = new Queue<FakeResponse>();
        }

        foreach (var response in responses)
        {
            queue.Enqueue(response);
        }

        return this;
    }

    public FakeScmServer Json(string method, string path, string json, Dictionary<string, string>? headers = null) =>
        On(method, path, new FakeResponse(HttpStatusCode.OK, json, Headers: headers));

    public FakeRequest Single(string method, string path) =>
        Requests.Single(request => request.Method.Method == method && request.Path == path);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(
            new FakeRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value)),
                body
            )
        );
        if (Fail?.Invoke(request) is { } failure)
        {
            throw failure;
        }

        var key = request.Method.Method + " " + request.RequestUri!.AbsolutePath;
        FakeResponse answer;
        if (_routes.TryGetValue(key, out var queue) && queue.Count > 0)
        {
            answer = queue.Dequeue();
            _last[key] = answer;
        }
        else if (!_last.TryGetValue(key, out answer!))
        {
            answer = new FakeResponse(HttpStatusCode.NotFound, """{"message":"404 Not found"}""");
        }

        if (answer.Delay > TimeSpan.Zero)
        {
            await Task.Delay(answer.Delay, cancellationToken);
        }

        var response = new HttpResponseMessage(answer.Status)
        {
            Content = new StringContent(answer.Body, Encoding.UTF8, answer.ContentType),
            RequestMessage = request,
        };
        foreach (var (name, value) in answer.Headers ?? [])
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}
