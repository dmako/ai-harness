using System.Text;
using System.Text.Json;

namespace Harness.Core.Hooks;

public sealed record HookCall(
    string Session,
    string Cwd,
    string Tool,
    string Kind,
    string McpServer,
    string McpTool,
    IReadOnlyList<string> Paths,
    string Task,
    string TaskId = "",
    string TaskAction = ""
);

/// <summary>What a harness task call did to its task, as <see cref="HookCall.TaskAction"/> carries it.</summary>
public static class HookTaskActions
{
    public const string Claim = "claim";
    public const string Update = "update";
    public const string Block = "block";
    public const string Release = "release";
    public const string Complete = "complete";
}

/// <summary>
/// Extracts only names and paths; never retains tool input, output, prompts or patch bodies. The one exception is
/// harness's own task tools: the task id of task_claim, task_update, task_complete and a claiming task_next, with
/// what the call did to it (<see cref="HookTaskActions"/>), so a hook event links its session to the task.
/// </summary>
public static class HookPayload
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public const int MaximumBytes = 1 << 20;

    public static HookCall Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaximumBytes)
        {
            throw new InvalidDataException("hook payload exceeds 1 MiB");
        }
        _ = Utf8.GetCharCount(payload.Span);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException("hook payload must be an object");
        }
        var session = RequiredString(root, "session_id");
        var cwd = NativePath(RequiredString(root, "cwd"));
        var tool = RequiredString(root, "tool_name");
        var input = root.TryGetProperty("tool_input", out var value) ? value : default;
        if (
            tool.StartsWith("mcp__", StringComparison.Ordinal)
            && tool.IndexOf("__", 5, StringComparison.Ordinal) is var separator
            && separator > 5
            && separator + 2 < tool.Length
        )
        {
            var server = tool[5..separator];
            var name = tool[(separator + 2)..];
            var (taskId, action) = server is "harness" ? TaskLink(name, input, root) : ("", "");
            return new(
                session,
                cwd,
                tool,
                "mcp",
                server,
                name,
                [],
                server is "harness" && name is "get_context" ? String(input, "task") : "",
                taskId,
                action
            );
        }
        var kind = tool switch
        {
            "Read" or "NotebookRead" => "read",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "apply_patch" => "edit",
            "Grep" or "Glob" or "LS" => "search",
            _ => ShellKind(ShellCommand(input)),
        };
        List<string> paths = [];
        if (tool is "apply_patch")
        {
            PatchPaths(input, paths, new HashSet<string>(StringComparer.Ordinal));
        }
        else if (kind is not "other")
        {
            foreach (var key in new[] { "file_path", "notebook_path", "path" })
            {
                if (String(input, key) is { Length: > 0 } path)
                {
                    paths.Add(NativePath(path));
                    break;
                }
            }
        }
        return new(session, cwd, tool, kind, "", "", paths, "");
    }

    // The task a harness task call worked on: the id in its answer (a claiming task_next names it only there), else
    // the id argument; nothing for a failed call, a task_next that did not claim, or any other tool.
    private static (string Id, string Action) TaskLink(string name, JsonElement input, JsonElement root)
    {
        var action = name switch
        {
            "task_claim" => HookTaskActions.Claim,
            "task_next"
                when input.ValueKind is JsonValueKind.Object
                    && input.TryGetProperty("claim", out var claim)
                    && claim.ValueKind is JsonValueKind.True => HookTaskActions.Claim,
            "task_update" => String(input, "status") switch
            {
                "blocked" => HookTaskActions.Block,
                "ready" => HookTaskActions.Release,
                _ => HookTaskActions.Update,
            },
            "task_complete" => HookTaskActions.Complete,
            _ => "",
        };
        if (action.Length is 0)
        {
            return ("", "");
        }
        var answer = root.TryGetProperty("tool_response", out var response) ? response : default;
        if (Failed(answer))
        {
            return ("", "");
        }
        var id = AnswerTaskId(answer, 0) ?? (name is "task_next" ? "" : String(input, "id")).Trim();
        return id.Length is > 0 and <= 128 && !id.Any(char.IsControl) ? (id, action) : ("", "");
    }

    // An MCP error answer: {"isError": true} (also as JSON text), or a Rust-style {"Err": …} result.
    private static bool Failed(JsonElement answer)
    {
        if (answer.ValueKind is JsonValueKind.String && Json(answer.GetString()) is { } parsed)
        {
            using (parsed)
            {
                return Failed(parsed.RootElement);
            }
        }
        return answer.ValueKind is JsonValueKind.Object
            && (
                answer.TryGetProperty("isError", out var error) && error.ValueKind is JsonValueKind.True
                || answer.TryGetProperty("Err", out _)
            );
    }

    // The id of the "task" object in a harness task answer, wherever the client put the answer: the result object,
    // its structuredContent, or a text content block (or plain string) holding the JSON.
    private static string? AnswerTaskId(JsonElement answer, int depth)
    {
        if (depth > 4)
        {
            return null;
        }
        switch (answer.ValueKind)
        {
            case JsonValueKind.Object:
                if (
                    answer.TryGetProperty("task", out var task)
                    && task.ValueKind is JsonValueKind.Object
                    && String(task, "id") is { Length: > 0 } id
                )
                {
                    return id;
                }
                foreach (var property in answer.EnumerateObject())
                {
                    if (AnswerTaskId(property.Value, depth + 1) is { } found)
                    {
                        return found;
                    }
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in answer.EnumerateArray())
                {
                    if (AnswerTaskId(item, depth + 1) is { } found)
                    {
                        return found;
                    }
                }
                return null;
            case JsonValueKind.String when Json(answer.GetString()) is { } parsed:
                using (parsed)
                {
                    return AnswerTaskId(parsed.RootElement, depth + 1);
                }
            default:
                return null;
        }
    }

    private static JsonDocument? Json(string? text)
    {
        if (text is null)
        {
            return null;
        }
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length is 0 || trimmed[0] is not ('{' or '['))
        {
            return null;
        }
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind is JsonValueKind.Null)
        {
            return "";
        }
        return property.ValueKind is JsonValueKind.String
            ? property.GetString()!
            : throw new InvalidDataException("invalid hook metadata type");
    }

    private static string String(JsonElement value, string key) =>
        value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(key, out var property)
        && property.ValueKind is JsonValueKind.String
            ? property.GetString()!
            : "";

    public static string NativePath(string path, bool? windows = null)
    {
        if (
            !(windows ?? OperatingSystem.IsWindows())
            || path.Length < 2
            || path[0] is not '/'
            || !char.IsAsciiLetter(path[1])
            || path.Length > 2 && path[2] is not '/'
        )
        {
            return path;
        }
        var rest = path.Length is 2 ? "\\" : path[2..].Replace('/', '\\');
        return char.ToUpperInvariant(path[1]) + ":" + rest;
    }

    private static string ShellCommand(JsonElement input)
    {
        if (input.ValueKind is not JsonValueKind.Object || !input.TryGetProperty("command", out var command))
        {
            return "";
        }
        if (command.ValueKind is JsonValueKind.String)
        {
            return command.GetString()!;
        }
        if (command.ValueKind is not JsonValueKind.Array)
        {
            return "";
        }
        var parts = command
            .EnumerateArray()
            .Where(v => v.ValueKind is JsonValueKind.String)
            .Select(v => v.GetString()!)
            .ToArray();
        return parts.Length >= 3 && (parts[1] is "-c" || parts[0] is "bash" or "sh" or "zsh" && parts[1] is "-lc")
            ? parts[^1]
            : string.Join(' ', parts);
    }

    private static string ShellKind(string command)
    {
        var kind = "other";
        foreach (var segment in command.Split('|', ';', '&', '\n'))
        {
            var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            while (
                i < words.Length
                && (words[i].Contains('=', StringComparison.Ordinal) || words[i] is "cd" or "sudo" or "time" or "xargs")
            )
            {
                i += words[i] is "cd" && i + 1 < words.Length ? 2 : 1;
            }
            if (i >= words.Length)
            {
                continue;
            }
            var program = words[i][(words[i].LastIndexOf('/', StringComparison.Ordinal) + 1)..];
            if (
                program is "grep" or "egrep" or "rg" or "ag" or "ack" or "find" or "fd" or "fgrep"
                || program is "git" && i + 1 < words.Length && words[i + 1] is "grep" or "ls-files"
            )
            {
                return "search";
            }
            if (program is "cat" or "head" or "tail" or "sed" or "awk" or "less" or "bat" or "nl")
            {
                kind = "read";
            }
        }
        return kind;
    }

    private static void PatchPaths(JsonElement value, List<string> paths, HashSet<string> seen)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                foreach (var line in value.GetString()!.Split('\n').Select(s => s.Trim()))
                {
                    foreach (
                        var prefix in new[]
                        {
                            "*** Add File: ",
                            "*** Update File: ",
                            "*** Delete File: ",
                            "*** Move to: ",
                        }
                    )
                    {
                        if (
                            line.StartsWith(prefix, StringComparison.Ordinal)
                            && line.Length > prefix.Length
                            && seen.Add(line[prefix.Length..])
                        )
                        {
                            paths.Add(line[prefix.Length..]);
                        }
                    }
                }
                break;
            case JsonValueKind.Object:
                foreach (var item in value.EnumerateObject())
                {
                    PatchPaths(item.Value, paths, seen);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    PatchPaths(item, paths, seen);
                }
                break;
        }
    }
}
