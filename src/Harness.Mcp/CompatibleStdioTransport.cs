using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Harness.Mcp;

internal sealed class CompatibleStdioTransport : ITransport
{
    internal const int MaximumFrameBytes = 16 * 1024 * 1024;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Input is caller-owned; the CLI owns the standard stream lifetime.")]
    private readonly Stream _input;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Output is caller-owned; the CLI owns the standard stream lifetime.")]
    private readonly Stream _output;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Channel<JsonRpcMessage> _messages = Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(64) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, int> _inflight = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private string? _protocolVersion;
    private TaskCompletionSource _empty = Completed();
    private readonly Task _reader;
    private int _disposed;

    internal CompatibleStdioTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _reader = ReadAsync();
    }

    public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;
    public string? SessionId => null;

    internal void CompleteCancelledRequest(RequestId requestId)
    {
        var id = JsonSerializer.SerializeToElement(requestId, McpJsonUtilities.DefaultOptions);
        lock (_inflight)
        {
            var key = Id(id);
            _inflight.Remove(key);
            _batches.Remove(key);
            if (_inflight.Count == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(JsonRpcMessage)));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("protocolVersion", out var version))
        {
            _protocolVersion = version.GetString();
        }

        string[] completed = [];
        if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id))
        {
            lock (_inflight)
            {
                var key = Id(id);
                if (_batches.TryGetValue(key, out var batch))
                {
                    batch.Responses[key] = root.Clone();
                    if (batch.Responses.Count != batch.Keys.Length)
                    {
                        return;
                    }

                    completed = batch.Keys;
                    bytes = JsonSerializer.SerializeToUtf8Bytes(batch.Keys.Select(name => batch.Responses[name]).ToArray());
                    foreach (var name in completed)
                    {
                        _batches.Remove(name);
                    }
                }
                else
                {
                    completed = [key];
                }
            }
        }

        await WriteFrameAsync(bytes, cancellationToken);
        lock (_inflight)
        {
            foreach (var key in completed)
            {
                if (!_inflight.TryGetValue(key, out var count))
                {
                    continue;
                }

                if (count == 1)
                {
                    _inflight.Remove(key);
                }
                else
                {
                    _inflight[key] = count - 1;
                }
            }

            if (_inflight.Count == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync();
        await _reader;
        _shutdown.Dispose();
        _writer.Dispose();
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            var buffer = new byte[64 * 1024];
            using var line = new MemoryStream();
            var oversized = false;
            while (true)
            {
                var count = await _input.ReadAsync(buffer, _shutdown.Token).AsTask().WaitAsync(_shutdown.Token);
                if (count == 0)
                {
                    if (line.Length > 0 || oversized)
                    {
                        await AcceptAsync(line.ToArray(), oversized);
                    }

                    Task empty;
                    lock (_inflight)
                    {
                        empty = _empty.Task;
                    }

                    try { await empty.WaitAsync(TimeSpan.FromSeconds(30), _shutdown.Token); }
                    catch (TimeoutException) { /* The SDK then cancels outstanding requests on disconnect. */ }
                    break;
                }

                var start = 0;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] != (byte)'\n')
                    {
                        continue;
                    }

                    Append(line, buffer.AsSpan(start, index - start + 1), ref oversized);
                    await AcceptAsync(line.ToArray(), oversized);
                    line.SetLength(0);
                    oversized = false;
                    start = index + 1;
                }

                Append(line, buffer.AsSpan(start, count - start), ref oversized);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Disposal cancels an idle reader and EOF drain without faulting the session.
        }
        catch (IOException exception) { failure = exception; }
        finally
        {
            _messages.Writer.TryComplete(failure);
        }
    }

    private static void Append(MemoryStream line, ReadOnlySpan<byte> part, ref bool oversized)
    {
        if (line.Length + part.Length > MaximumFrameBytes)
        {
            oversized = true;
            line.SetLength(0);
        }

        if (!oversized)
        {
            line.Write(part);
        }
    }

    private async Task AcceptAsync(byte[] frame, bool oversized)
    {
        if (oversized)
        {
            await ReplyAsync(null, -32700, "frame exceeds maximum size");
            return;
        }

        if (frame.All(value => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
        {
            return;
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(frame, new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException)
        {
            await ReplyAsync(null, -32700, "parse error: invalid JSON");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var messages = root.EnumerateArray().ToArray();
                if (messages.Length == 0 || messages.Any(message => !Valid(message)))
                {
                    await ReplyAsync(null, -32600, messages.Length == 0 ? "invalid request: empty or malformed batch" : "invalid request: batch contains a non-JSON-RPC 2.0 message");
                    return;
                }

                if (string.CompareOrdinal(_protocolVersion, "2025-06-18") >= 0)
                {
                    throw new IOException($"JSON-RPC batching is not supported in 2025-06-18 and later (request version: {_protocolVersion})");
                }

                var keys = messages.Where(message => message.TryGetProperty("method", out _) && message.TryGetProperty("id", out _)).Select(message => Id(message.GetProperty("id"))).ToArray();
                var batch = new Batch(keys);
                lock (_inflight)
                {
                    if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(key => _batches.ContainsKey(key)))
                    {
                        throw new IOException("batch contains previously seen request IDs");
                    }

                    foreach (var key in keys)
                    {
                        _batches[key] = batch;
                    }
                }

                foreach (var message in messages)
                {
                    await ForwardAsync(message);
                }
            }
            else if (!Valid(root))
            {
                var id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var value) && ValidId(value) ? value : (JsonElement?)null;
                await ReplyAsync(id, -32600, root.ValueKind == JsonValueKind.Object ? "invalid request: not a JSON-RPC 2.0 message" : "invalid request: expected a JSON object");
            }
            else
            {
                await ForwardAsync(root);
            }
        }
    }

    private async Task ForwardAsync(JsonElement message)
    {
        JsonRpcMessage? decoded;
        try { decoded = message.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions); }
        catch (JsonException)
        {
            await ReplyAsync(message.TryGetProperty("id", out var value) && ValidId(value) ? value : null, -32600, "invalid request: not a JSON-RPC 2.0 message");
            return;
        }

        if (decoded is null)
        {
            return;
        }

        if (message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var id))
        {
            lock (_inflight)
            {
                if (_inflight.Count == 0)
                {
                    _empty = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                var key = Id(id);
                _inflight[key] = _inflight.GetValueOrDefault(key) + 1;
            }
        }

        await _messages.Writer.WriteAsync(decoded, _shutdown.Token);
    }

    private static bool Valid(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
        {
            return false;
        }

        var hasId = message.TryGetProperty("id", out var id);
        if (message.TryGetProperty("method", out var method))
        {
            return method.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(method.GetString()) && (!hasId || ValidId(id));
        }

        return hasId && ValidId(id) && message.TryGetProperty("result", out _) != message.TryGetProperty("error", out _);
    }

    private static bool ValidId(JsonElement id) => id.ValueKind == JsonValueKind.String || id.ValueKind == JsonValueKind.Number && id.TryGetDouble(out var number) && double.IsFinite(number);
    private static string Id(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private Task ReplyAsync(JsonElement? id, int code, string message) => WriteFrameAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, error = new { code, message } }), _shutdown.Token);

    private async Task WriteFrameAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken);
            await _output.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            _messages.Writer.TryComplete();
            await _shutdown.CancelAsync();
            throw;
        }
        finally { _writer.Release(); }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private sealed class Batch(string[] keys)
    {
        internal string[] Keys { get; } = keys;
        internal Dictionary<string, JsonElement> Responses { get; } = new(StringComparer.Ordinal);
    }
}
