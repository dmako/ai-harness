using System.Net;
using System.Text;
using System.Threading.Channels;

namespace Harness.Ui.Tests;

/// <summary>
/// harness ui's API as fixed answers: GET answers by path, POSTs recorded, and /api/events a stream the test writes
/// change events into.
/// </summary>
internal sealed class FixtureApi : HttpMessageHandler
{
    private readonly Channel<string> _events = Channel.CreateUnbounded<string>();

    public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);
    public List<(string Path, string Body, string? Origin, string? Ui)> Posts { get; } = [];
    public List<string> Gets { get; } = [];

    /// <summary>Sends an "event: change" with <paramref name="json"/> to the open stream.</summary>
    public void Change(string json) => _events.Writer.TryWrite($"event: change\ndata: {json}\n\n");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri!.AbsolutePath;
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
        return Answers.TryGetValue(path, out var answer)
            ? Json(answer)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"unknown endpoint\"}") };
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
