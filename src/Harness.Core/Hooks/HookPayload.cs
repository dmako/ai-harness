using System.Text;
using System.Text.Json;

namespace Harness.Core.Hooks;

public sealed record HookCall(string Session, string Cwd, string Tool, string Kind, string McpServer, string McpTool, IReadOnlyList<string> Paths, string Task);

/// <summary>Extracts only names and paths; never retains tool input, output, prompts or patch bodies.</summary>
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
        if (tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.IndexOf("__", 5, StringComparison.Ordinal) is var separator && separator > 5 && separator + 2 < tool.Length)
        {
            var server = tool[5..separator];
            var name = tool[(separator + 2)..];
            return new(session, cwd, tool, "mcp", server, name, [], server is "harness" && name is "get_context" ? String(input, "task") : "");
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
    private static string RequiredString(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind is JsonValueKind.Null)
        {
            return "";
        }
        return property.ValueKind is JsonValueKind.String ? property.GetString()! : throw new InvalidDataException("invalid hook metadata type");
    }
    private static string String(JsonElement value, string key) => value.ValueKind is JsonValueKind.Object && value.TryGetProperty(key, out var property) && property.ValueKind is JsonValueKind.String ? property.GetString()! : "";
    public static string NativePath(string path, bool? windows = null)
    {
        if (!(windows ?? OperatingSystem.IsWindows()) || path.Length < 2 || path[0] is not '/' || !char.IsAsciiLetter(path[1]) || path.Length > 2 && path[2] is not '/')
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
        var parts = command.EnumerateArray().Where(v => v.ValueKind is JsonValueKind.String).Select(v => v.GetString()!).ToArray();
        return parts.Length >= 3 && (parts[1] is "-c" || parts[0] is "bash" or "sh" or "zsh" && parts[1] is "-lc") ? parts[^1] : string.Join(' ', parts);
    }
    private static string ShellKind(string command)
    {
        var kind = "other";
        foreach (var segment in command.Split('|', ';', '&', '\n'))
        {
            var words = segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            while (i < words.Length && (words[i].Contains('=', StringComparison.Ordinal) || words[i] is "cd" or "sudo" or "time" or "xargs"))
            {
                i += words[i] is "cd" && i + 1 < words.Length ? 2 : 1;
            }
            if (i >= words.Length)
            {
                continue;
            }
            var program = words[i][(words[i].LastIndexOf('/') + 1)..];
            if (program is "grep" or "egrep" or "rg" or "ag" or "ack" or "find" or "fd" or "fgrep" || program is "git" && i + 1 < words.Length && words[i + 1] is "grep" or "ls-files")
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
                    foreach (var prefix in new[] { "*** Add File: ", "*** Update File: ", "*** Delete File: ", "*** Move to: " })
                    {
                        if (line.StartsWith(prefix, StringComparison.Ordinal) && line.Length > prefix.Length && seen.Add(line[prefix.Length..]))
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
