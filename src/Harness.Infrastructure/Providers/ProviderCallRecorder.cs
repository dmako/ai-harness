using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Harness.Core.Memory;
using Harness.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Harness.Infrastructure.Providers;

/// <summary>Bounded best-effort observations off the request path; never initializes project storage.</summary>
public sealed class ProviderCallRecorder : IAsyncDisposable
{
    private readonly Channel<(ProviderResult Result, ProviderRequest Request, DateTimeOffset At)> _queue = Channel.CreateBounded<(ProviderResult, ProviderRequest, DateTimeOffset)>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _writer;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _closeGate = new();
    private Task? _closeTask;

    public ProviderCallRecorder(string databasePath, int busyTimeoutMs = 5000) => _writer = WriteAsync(databasePath, Math.Clamp(busyTimeoutMs, 0, 2000));
    public void Observe(ProviderResult result, ProviderRequest request) => _queue.Writer.TryWrite((result, request, DateTimeOffset.UtcNow));
    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
            return new(_closeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        _queue.Writer.TryComplete();
        _stop.CancelAfter(TimeSpan.FromSeconds(2));
        try
        { await _writer.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { /* Discard observations beyond the shutdown budget. */ }
        finally { _stop.Dispose(); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "PRAGMA accepts no parameters; busyTimeoutMs is a clamped integer, never SQL text.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "S2077:Formatting SQL queries is security-sensitive", Justification = "Only a clamped integer is formatted into PRAGMA; all stored values use parameters.")]
    private async Task WriteAsync(string path, int busyTimeoutMs)
    {
        await foreach (var (result, request, at) in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                continue;
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()) { DefaultTimeout = -1 };
                await connection.OpenAsync(_stop.Token).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandTimeout = -1;
                command.CommandText = $"PRAGMA busy_timeout={busyTimeoutMs}";
                await command.ExecuteNonQueryAsync(_stop.Token).ConfigureAwait(false);
                command.CommandText = """
                    INSERT INTO provider_calls(at, provider, capability, query, items, response_bytes, duration_ms, error, timeout)
                    VALUES($at,$provider,$capability,$query,$items,$bytes,$duration,$error,$timeout);
                    DELETE FROM provider_calls WHERE (SELECT MAX(id) FROM provider_calls) % 100 = 0 AND id <= (SELECT MAX(id)-10000 FROM provider_calls);
                    """;
                command.Parameters.AddWithValue("$at", at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$provider", result.Provider);
                command.Parameters.AddWithValue("$capability", result.Capability);
                command.Parameters.AddWithValue("$query", Prefix(CredentialRedactor.Redact(request.Query), 200));
                command.Parameters.AddWithValue("$items", result.Items.Count);
                command.Parameters.AddWithValue("$bytes", result.Bytes);
                command.Parameters.AddWithValue("$duration", (long)result.Latency.TotalMilliseconds);
                command.Parameters.AddWithValue("$error", result.Error is { } error ? Prefix(CredentialRedactor.Redact(error.Message), 1000) : DBNull.Value);
                command.Parameters.AddWithValue("$timeout", result.TimedOut);
                await command.ExecuteNonQueryAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SqliteException or UnauthorizedAccessException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { /* Local metrics are optional; the caller already received its result. */ }
        }
    }

    private static string Prefix(string value, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= bytes)
            return value;
        var output = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Utf8SequenceLength > bytes)
                break;
            output.Append(rune.ToString());
            bytes -= rune.Utf8SequenceLength;
        }
        return output + "…";
    }
}
