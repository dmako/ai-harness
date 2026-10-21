using System.Text;
using System.Text.Json.Nodes;
using Harness.Core.Instructions;

namespace Harness.Infrastructure.Clients;

/// <summary>Ownership-aware removal of existing client hooks, shared with the hook command port.</summary>
public static class ClientHookCleanup
{
    public static bool IsHarness(string command, string subcommand)
    {
        var (exe, rest) = SplitExecutable(command);
        var words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = Path.GetFileName(exe);
        return name is "harness" or "harness.exe" && words.Length >= 2 && words[0] is "hook" && words[1] == subcommand;
    }
    public static (string Executable, string Remainder) SplitExecutable(string command)
    {
        command = command.Trim();
        if (command.Length is 0)
            return ("", "");
        if (command[0] is not ('\'' or '"'))
        {
            var space = command.IndexOf(' ', StringComparison.Ordinal);
            return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
        }
        var quote = command[0];
        var result = new StringBuilder();
        var i = 1;
        while (i < command.Length)
        {
            if (quote is '\'' && command.AsSpan(i).StartsWith("'\\''", StringComparison.Ordinal))
            { result.Append('\''); i += 4; continue; }
            if (command[i] == quote)
                return (result.ToString(), command[(i + 1)..]);
            result.Append(command[i]);
            i++;
        }
        return (result.ToString(), "");
    }
    public static FileChange Remove(string path, string client, bool dryRun = false) => FileEditor.Apply(path, (source, exists) =>
    {
        if (!exists || string.IsNullOrWhiteSpace(source))
            return new(source, "absent");
        var output = source;
        var removed = false;
        foreach (var (eventName, subcommand) in client is "claude-code" ? new[] { ("PostToolUse", "event"), ("SessionStart", "session") } : [("PostToolUse", "event")])
        {
            var root = JsonConfigurationEditor.ParseObject(output);
            if (!root.TryGetPropertyValue("hooks", out var node))
                continue;
            if (node is not JsonObject hooks)
                throw new InvalidDataException("hooks is not an object");
            if (!hooks.TryGetPropertyValue(eventName, out var raw) || raw is null)
                continue;
            if (raw is not JsonArray groups)
                throw new InvalidDataException("hooks." + eventName + " is not an array");
            var rest = WithoutHarness(groups, subcommand);
            if (JsonNode.DeepEquals(groups, rest))
                continue;
            removed = true;
            output = rest.Count > 0 ? JsonConfigurationEditor.Set(output, ["hooks"], eventName, rest).Text : JsonConfigurationEditor.Delete(output, ["hooks"], eventName).Text;
            if (JsonConfigurationEditor.ParseObject(output)["hooks"] is JsonObject { Count: 0 })
                output = JsonConfigurationEditor.Delete(output, [], "hooks").Text;
        }
        return new(output, removed ? "remove" : "absent");
    }, dryRun, backup: true);
    private static JsonArray WithoutHarness(JsonArray groups, string subcommand)
    {
        JsonArray result = [];
        foreach (var group in groups)
        {
            if (group is not JsonObject obj || obj["hooks"] is not JsonArray items)
            { result.Add(group?.DeepClone()); continue; }
            var rest = items.Where(item => item is not JsonObject hook || hook["type"]?.GetValue<string>() is not "command" || !IsHarness(hook["command"]?.GetValue<string>() ?? "", subcommand)).Select(item => item?.DeepClone()).ToArray();
            if (rest.Length == items.Count)
                result.Add(group.DeepClone());
            else if (rest.Length > 0)
            {
                var copy = (JsonObject)group.DeepClone();
                copy["hooks"] = new JsonArray(rest);
                result.Add(copy);
            }
        }
        return result;
    }
}
