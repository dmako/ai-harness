using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Harness.Integration.Web;

/// <summary>
/// A GitLab API v4 stand-in on a localhost port, for front ends that run in another process (harness ui, the CLI):
/// /api/v4/version and /api/v4/user answer with the expected token in PRIVATE-TOKEN and 401 otherwise; anything else
/// is 404, so a Forgejo connection to it is told it is the wrong server type.
/// </summary>
internal sealed class FakeGitlabServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _serving;
    private readonly string _token;

    internal FakeGitlabServer(string token)
    {
        _token = token;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    internal int Port { get; }

    internal string Url => $"http://localhost:{Port}";

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

            var authorized = context.Request.Headers["PRIVATE-TOKEN"] == _token;
            var (status, body) = context.Request.Url!.AbsolutePath switch
            {
                "/api/v4/version" or "/api/v4/user" when !authorized => (401, """{"message":"401 Unauthorized"}"""),
                "/api/v4/version" => (200, """{"version":"17.5.0-ee","revision":"fake"}"""),
                "/api/v4/user" => (200, """{"id":7,"username":"reviewer","name":"Review Bot"}"""),
                _ => (404, """{"message":"404 Not Found"}"""),
            };
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Close();
        await _serving;
    }
}
