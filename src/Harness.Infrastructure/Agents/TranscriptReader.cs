using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Core.Tasks;

namespace Harness.Infrastructure.Agents;

/// <summary>One moment of a session's activity: the tokens it used then and the active time that led to it.</summary>
public sealed record UsageEntry(DateTimeOffset At, string Model, TokenCounts Tokens, long ActiveMs);

/// <summary>Where a read of one transcript file ended, and the usage it found.</summary>
public sealed record TranscriptRead(long Offset, JsonObject State, IReadOnlyList<UsageEntry> Entries, bool Rotated);

/// <summary>
/// Reads the usage in a Claude Code transcript or a Codex rollout from a byte offset to its last complete line.
/// Only counts, model names and times leave a line, never its content. The state carries what the next read
/// continues from: the last Claude message id (a message's blocks repeat its usage on several lines) and activity
/// time, the current Codex model and its last cumulative totals.
/// </summary>
public static class TranscriptReader
{
    /// <summary>A gap within a turn longer than this (waiting for a permission, a stuck tool) counts only this.</summary>
    public static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(10);

    private const int Chunk = 1 << 20;

    public static TranscriptRead Read(
        string client,
        string sessionId,
        string path,
        long offset,
        JsonObject state,
        CancellationToken cancellationToken = default
    )
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1,
            FileOptions.SequentialScan
        );
        var rotated = stream.Length < offset;
        if (rotated)
        {
            (offset, state) = (0, []);
        }
        else
        {
            state = (JsonObject)state.DeepClone();
        }
        stream.Position = offset;
        List<UsageEntry> entries = [];
        var buffer = new byte[Chunk];
        var pending = new MemoryStream();
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] is not (byte)'\n')
                {
                    continue;
                }
                ReadOnlyMemory<byte> line;
                if (pending.Length > 0)
                {
                    pending.Write(buffer, start, index - start);
                    line = pending.ToArray();
                    pending.SetLength(0);
                }
                else
                {
                    line = buffer.AsMemory(start, index - start);
                }
                offset += line.Length + 1;
                start = index + 1;
                if (Line(client, sessionId, line, state) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            pending.Write(buffer, start, read - start);
        }
        return new(offset, state, entries, rotated);
    }

    private static UsageEntry? Line(string client, string sessionId, ReadOnlyMemory<byte> line, JsonObject state)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
        using (document)
        {
            var root = document.RootElement;
            if (
                root.ValueKind is not JsonValueKind.Object
                || Text(root, "timestamp") is not { Length: > 0 } stamp
                || !DateTimeOffset.TryParse(
                    stamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var at
                )
            )
            {
                return null;
            }
            return client is "codex" ? Codex(root, at, state) : Claude(root, sessionId, at, state);
        }
    }

    private static UsageEntry? Claude(JsonElement root, string sessionId, DateTimeOffset at, JsonObject state)
    {
        var type = Text(root, "type");
        // A resumed or forked session's file starts with the history it continues, written under the earlier id.
        if (type is not ("assistant" or "user") || Text(root, "sessionId") is { Length: > 0 } id && id != sessionId)
        {
            return null;
        }
        var prompt = type is "user" && Prompt(root);
        long active = 0;
        if (!prompt && Time(state, "last_at") is { } last && at > last)
        {
            active = (long)Math.Min((at - last).TotalMilliseconds, MaximumGap.TotalMilliseconds);
        }
        if (Time(state, "last_at") is not { } previous || at > previous)
        {
            state["last_at"] = at.ToString("O", CultureInfo.InvariantCulture);
        }
        var model = state["model"]?.GetValue<string>() ?? "";
        var tokens = TokenCounts.Zero;
        if (
            type is "assistant"
            && root.TryGetProperty("message", out var message)
            && message.ValueKind is JsonValueKind.Object
        )
        {
            if (Text(message, "model") is { Length: > 0 } named && !named.StartsWith('<', StringComparison.Ordinal))
            {
                state["model"] = model = named;
            }
            var messageId = Text(message, "id");
            if (
                (messageId.Length is 0 || messageId != state["last_id"]?.GetValue<string>())
                && message.TryGetProperty("usage", out var usage)
                && usage.ValueKind is JsonValueKind.Object
            )
            {
                tokens = new(
                    Number(usage, "input_tokens"),
                    Number(usage, "cache_read_input_tokens"),
                    Number(usage, "cache_creation_input_tokens"),
                    Number(usage, "output_tokens"),
                    usage.TryGetProperty("output_tokens_details", out var details)
                        ? Number(details, "thinking_tokens")
                        : 0
                );
            }
            if (messageId.Length > 0)
            {
                state["last_id"] = messageId;
            }
        }
        return new(at, model, tokens, active);
    }

    // A prompt starts a turn: the user's own message, not a tool result or harness-added meta text.
    private static bool Prompt(JsonElement root)
    {
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind is JsonValueKind.True)
        {
            return false;
        }
        if (!root.TryGetProperty("message", out var message) || message.ValueKind is not JsonValueKind.Object)
        {
            return false;
        }
        if (!message.TryGetProperty("content", out var content))
        {
            return false;
        }
        return content.ValueKind switch
        {
            JsonValueKind.String => true,
            JsonValueKind.Array => !content
                .EnumerateArray()
                .Any(block => block.ValueKind is JsonValueKind.Object && Text(block, "type") is "tool_result"),
            _ => false,
        };
    }

    private static UsageEntry? Codex(JsonElement root, DateTimeOffset at, JsonObject state)
    {
        var payload = root.TryGetProperty("payload", out var value) ? value : default;
        if (payload.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }
        var model = state["model"]?.GetValue<string>() ?? "";
        switch (Text(root, "type"), Text(payload, "type"))
        {
            case ("turn_context", _):
                if (Text(payload, "model") is { Length: > 0 } named)
                {
                    state["model"] = named;
                }
                return null;
            case ("event_msg", "task_started"):
                return new(at, model, TokenCounts.Zero, 0);
            case ("event_msg", "task_complete"):
                return new(at, model, TokenCounts.Zero, Math.Max(0, Number(payload, "duration_ms")));
            case ("event_msg", "token_count")
                when payload.TryGetProperty("info", out var info)
                    && info.ValueKind is JsonValueKind.Object
                    && info.TryGetProperty("total_token_usage", out var total)
                    && total.ValueKind is JsonValueKind.Object:
                // Codex counts cumulatively, and its input includes what was read from or written to the cache.
                var input = Number(total, "input_tokens");
                var cached = Number(total, "cached_input_tokens");
                var written = Number(total, "cache_write_input_tokens");
                var now = new TokenCounts(
                    Math.Max(0, input - cached - written),
                    cached,
                    written,
                    Number(total, "output_tokens"),
                    Number(total, "reasoning_output_tokens")
                );
                var before =
                    state["totals"] is JsonArray saved && saved.Count is 5
                        ? new TokenCounts(
                            saved[0]!.GetValue<long>(),
                            saved[1]!.GetValue<long>(),
                            saved[2]!.GetValue<long>(),
                            saved[3]!.GetValue<long>(),
                            saved[4]!.GetValue<long>()
                        )
                        : TokenCounts.Zero;
                state["totals"] = new JsonArray(now.Input, now.CachedInput, now.CacheWrite, now.Output, now.Reasoning);
                // Counters that went back started again (a new process on the same rollout): all of it is new.
                var restarted =
                    now.Input < before.Input
                    || now.CachedInput < before.CachedInput
                    || now.CacheWrite < before.CacheWrite
                    || now.Output < before.Output
                    || now.Reasoning < before.Reasoning;
                var delta = restarted
                    ? now
                    : new TokenCounts(
                        now.Input - before.Input,
                        now.CachedInput - before.CachedInput,
                        now.CacheWrite - before.CacheWrite,
                        now.Output - before.Output,
                        now.Reasoning - before.Reasoning
                    );
                return new(at, model, delta, 0);
            default:
                return null;
        }
    }

    private static DateTimeOffset? Time(JsonObject state, string key) =>
        state[key]?.GetValue<string>() is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    private static string Text(JsonElement value, string key) =>
        value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(key, out var property)
        && property.ValueKind is JsonValueKind.String
            ? property.GetString()!
            : "";

    private static long Number(JsonElement value, string key) =>
        value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(key, out var property)
        && property.ValueKind is JsonValueKind.Number
        && property.TryGetInt64(out var number)
            ? number
            : 0;
}
