using System.Net;
using System.Text;
using System.Threading.Channels;

namespace Harness.Ui.Tests;

/// <summary>
/// harness ui's API as fixed answers. A GET is answered from <see cref="Answers"/> (a concrete path, or a route
/// template such as /api/projects/{id}/board), else from the fixture file at the route's path under Fixtures/
/// (api/projects/{id}/board.json). POSTs are recorded and answered with {}; /api/events is a stream the test
/// writes change events into.
/// </summary>
internal sealed class FixtureApi : HttpMessageHandler
{
    private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, string> _files = Load();

    /// <summary>Answers by concrete path or route template; they win over the fixture files.</summary>
    public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers delayed by the given time (a slow endpoint), by concrete path or template.</summary>
    public Dictionary<string, TimeSpan> Delays { get; } = new(StringComparer.Ordinal);

    /// <summary>Endpoints answered with this status and {"error": …} instead, by concrete path or template.</summary>
    public Dictionary<string, HttpStatusCode> Failures { get; } = new(StringComparer.Ordinal);

    public List<(string Path, string Body, string? Origin, string? Ui)> Posts { get; } = [];
    public List<string> Gets { get; } = [];

    /// <summary>Sends an "event: change" with <paramref name="json"/> to the open stream.</summary>
    public void Change(string json) => _events.Writer.TryWrite($"event: change\ndata: {json}\n\n");

    /// <summary>Sends an "event: agent" with <paramref name="json"/> to the open stream.</summary>
    public void Agent(string json) => _events.Writer.TryWrite($"event: agent\ndata: {json}\n\n");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri!.AbsolutePath;
        if (Find(Delays, path) is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }
        if (Find(Failures, path) is { } status)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent($"{{\"error\":\"fixture failure {(int)status}\"}}", Encoding.UTF8),
            };
        }
        if (request.Method == HttpMethod.Post)
        {
            Posts.Add(
                (
                    path,
                    await request.Content!.ReadAsStringAsync(cancellationToken),
                    request.Headers.TryGetValues("Origin", out var o) ? o.Single() : null,
                    request.Headers.TryGetValues("X-Harness-UI", out var u) ? u.Single() : null
                )
            );
            return Json("{}");
        }
        Gets.Add(path);
        if (path is "/api/events")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EventStream(_events.Reader)) };
        }
        return (Find(Answers, path) ?? Find(_files, path)) is { } answer
            ? Json(answer)
            : new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"unknown endpoint\"}"),
            };
    }

    /// <summary>The value for <paramref name="path"/>: its own entry, else the first template entry it matches.</summary>
    private static T? Find<T>(Dictionary<string, T> entries, string path)
        where T : struct
    {
        if (entries.TryGetValue(path, out var exact))
        {
            return exact;
        }
        return FirstMatch(entries, path) is { } key ? entries[key] : null;
    }

    private static string? Find(Dictionary<string, string> entries, string path)
    {
        if (entries.TryGetValue(path, out var exact))
        {
            return exact;
        }
        return FirstMatch(entries, path) is { } key ? entries[key] : null;
    }

    private static string? FirstMatch<T>(Dictionary<string, T> entries, string path) =>
        entries.Keys.Where(k => k.Contains('{', StringComparison.Ordinal)).FirstOrDefault(k => Matches(k, path));

    /// <summary>Whether <paramref name="path"/> fits <paramref name="template"/>: equal segments, {name} for any.</summary>
    public static bool Matches(string template, string path)
    {
        var t = template.Split('/');
        var p = path.Split('/');
        return t.Length == p.Length
            && t.Zip(p).All(s => s.First == s.Second || (s.First.StartsWith('{', StringComparison.Ordinal) && s.First.EndsWith('}', StringComparison.Ordinal) && s.Second.Length > 0));
    }

    /// <summary>The fixture files by route: Fixtures/api/projects/{id}/board.json answers /api/projects/{id}/board.</summary>
    private static Dictionary<string, string> Load()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(Fixtures.Folder))
        {
            return files;
        }
        foreach (var file in Directory.EnumerateFiles(Fixtures.Folder, "*.json", SearchOption.AllDirectories))
        {
            var route = "/" + Path.GetRelativePath(Fixtures.Folder, file).Replace('\\', '/')[..^".json".Length];
            files[route] = File.ReadAllText(file);
        }
        return files;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A never-ending response body fed from a channel, like /api/events.</summary>
    private sealed class EventStream(ChannelReader<string> reader) : Stream
    {
        private byte[] _buffer = Encoding.UTF8.GetBytes(": harness live updates\nretry: 3000\n\n");
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _buffer.Length)
            {
                _buffer = Encoding.UTF8.GetBytes(await reader.ReadAsync(cancellationToken));
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _buffer.Length - _offset);
            _buffer.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
