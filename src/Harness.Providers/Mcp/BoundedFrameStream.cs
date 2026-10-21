namespace Harness.Providers.Mcp;

internal sealed class BoundedFrameStream(Stream inner, int limit, Action oversized) : Stream
{
    private int _length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Check(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Check(buffer.Span[..count]);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
    private int Check(ReadOnlySpan<byte> buffer)
    {
        foreach (var value in buffer)
        {
            _length = value is (byte)'\n' ? 0 : checked(_length + 1);
            if (_length > limit)
            {
                oversized();
                throw new InvalidDataException("Provider output exceeded the maximum MCP frame size.");
            }
        }
        return buffer.Length;
    }
}
