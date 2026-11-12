using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Harness.Integration.Web;

/// <summary>A request the fake GitLab received: method, raw path (with %2F as sent), body and token header.</summary>
internal sealed record FakeGitlabRequest(string Method, string Path, string Body, string? Token);

/// <summary>
/// A GitLab API v4 stand-in on a localhost port, for front ends that run in another process (harness ui, harness
/// mcp): /api/v4/version and /api/v4/user, plus any canned routes ("METHOD /raw/path" to a JSON body), answer with
/// the expected token in PRIVATE-TOKEN and 401 otherwise; anything else is 404, so a Forgejo connection to it is told
/// it is the wrong server type.
/// </summary>
internal sealed class FakeGitlabServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serving;
    private readonly string _token;
    private readonly IReadOnlyDictionary<string, string> _routes;

    internal FakeGitlabServer(string token, IReadOnlyDictionary<string, string>? routes = null)
    {
        _token = token;
        _routes = routes ?? new Dictionary<string, string>();
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        _listener.Prefixes.Add($"http://{Host}:{Port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    internal int Port { get; }

    /// <summary>
    /// The host the server listens on: localhost on Windows, where http.sys lets any user listen there; the loopback
    /// address elsewhere, since the managed HttpListener of Linux and macOS resolves "localhost" again when it closes
    /// and may pick the other loopback address, failing the close with "Address already in use".
    /// </summary>
    internal static string Host => OperatingSystem.IsWindows() ? "localhost" : "127.0.0.1";

    internal string Url => $"http://{Host}:{Port}";

    internal ConcurrentQueue<FakeGitlabRequest> Requests { get; } = new();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            var request = context.Request;
            var path = (request.RawUrl ?? "/").Split('?', 2)[0];
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            var token = request.Headers["PRIVATE-TOKEN"];
            Requests.Enqueue(new FakeGitlabRequest(request.HttpMethod, path, body, token));
            var known =
                path is "/api/v4/version" or "/api/v4/user" || _routes.ContainsKey(request.HttpMethod + " " + path);
            var (status, answer) = (known, token == _token, path) switch
            {
                (true, false, _) => (401, """{"message":"401 Unauthorized"}"""),
                (_, _, "/api/v4/version") => (200, """{"version":"17.5.0-ee","revision":"fake"}"""),
                (_, _, "/api/v4/user") => (200, """{"id":7,"username":"reviewer","name":"Review Bot"}"""),
                (true, true, _) => (request.HttpMethod is "POST" ? 201 : 200, _routes[request.HttpMethod + " " + path]),
                _ => (404, """{"message":"404 Not Found"}"""),
            };
            var bytes = Encoding.UTF8.GetBytes(answer);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try
        {
            _listener.Close();
        }
        catch (HttpListenerException)
        {
            // the listener has stopped serving; a failure to release its prefix must not fail the test
        }

        await _serving;
    }
}
