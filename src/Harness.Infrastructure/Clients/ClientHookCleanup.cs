using System.Text;
using System.Text.Json.Nodes;
using Harness.Core.Instructions;

namespace Harness.Infrastructure.Clients;

/// <summary>Ownership-aware editing of client hooks.</summary>
public static class ClientHookCleanup
{
    public const string CodexTrustNote = "Codex runs new hooks only after you trust them: run /hooks in Codex and approve harness's.";
    public static IReadOnlyList<ClientTarget> Targets(ClientPaths paths, string client, string scope, string root)
    {
        if (client is not ("claude-code" or "codex"))
        {
            throw new ArgumentException("unknown hook client: " + client);
        }
        var scopes = client is "claude-code" ? new[] { "user", "project", "local" } : ["user", "project"];
        if (scope is not "all" && !scopes.Contains(scope, StringComparer.Ordinal))
        {
            throw new ArgumentException(client + " has no " + scope + " scope for hooks");
        }
        return scopes.Where(s => scope is "all" || s == scope).Select(s => new ClientTarget(client, s, TargetPath(paths, client, s, root), [])).ToArray();
    }
    private static string TargetPath(ClientPaths paths, string client, string scope, string root)
    {
        if (client is "codex")
        {
            var directory = scope is "user" ? Path.GetDirectoryName(paths.CodexFile)! : Path.Combine(root, ".codex");
            return Path.Combine(directory, "hooks.json");
        }
        var claude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var user = string.IsNullOrEmpty(claude) ? Path.Combine(paths.Home, ".claude") : claude;
        var folder = scope is "user" ? user : Path.Combine(root, ".claude");
        return Path.Combine(folder, scope is "local" ? "settings.local.json" : "settings.json");
    }

    public static string Command(string executable, string client, bool? windows = null)
    {
        if (client is not ("claude-code" or "codex"))
        {
            throw new ArgumentException("unknown hook client", nameof(client));
        }
        if (executable.IndexOfAny(['\r', '\n', '\0']) >= 0 || (windows ?? OperatingSystem.IsWindows()) && executable.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("invalid hook executable path", nameof(executable));
        }
        var quoted = executable;
        if (windows ?? OperatingSystem.IsWindows())
        {
            quoted = '"' + executable.Replace("$", "\\$", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal) + '"';
        }
        else if (executable.Any(c => !char.IsLetterOrDigit(c) && c is not ('/' or '_' or '-' or '.')))
        {
            quoted = "'" + executable.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }
        return quoted + " hook event --client " + client;
    }
    public static string Installed(string path, string subcommand = "event")
    {
        if (!File.Exists(path))
        {
            return "";
        }
        var text = FileEditor.Read(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }
        var root = JsonConfigurationEditor.ParseObject(text);
        if (root["hooks"] is not JsonObject hooks || hooks[subcommand is "session" ? "SessionStart" : "PostToolUse"] is not JsonArray groups)
        {
            return "";
        }
        return groups.OfType<JsonObject>().SelectMany(g => g["hooks"] is JsonArray items ? items : []).OfType<JsonObject>().Where(h => Text(h["type"]) is "command" && IsHarness(Text(h["command"]), subcommand)).Select(h => Text(h["command"])).FirstOrDefault() ?? "";
    }
    public static string CommandProblem(string command)
    {
        if (command.Length is 0)
        {
            return "";
        }
        var (exe, _) = SplitExecutable(command);
        if (command[0] is not ('"' or '\'') && exe.Contains('\\', StringComparison.Ordinal))
        {
            return "the executable's path is not quoted: Git Bash drops its backslashes; run 'harness hooks install' again";
        }
        return Path.IsPathRooted(exe) && !File.Exists(new ClientConfiguration(ClientPaths.Current()).Expand(exe)) ? "executable does not exist; run 'harness hooks install' again" : "";
    }
    public static FileChange Install(string path, string client, string command, bool dryRun = false) => Edit(path, client, command, dryRun);
    public static FileChange Install(ClientTarget target, string command, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Edit(target.Path, target.Client, command, dryRun, () => Missing(target));
    }
    public static FileChange Remove(string path, string client, bool dryRun = false) => Edit(path, client, "", dryRun);
    public static FileChange Remove(ClientTarget target, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Edit(target.Path, target.Client, "", dryRun, () => Missing(target));
    }
    private static string Missing(ClientTarget target) => target.Scope is "user" && !Directory.Exists(Path.GetDirectoryName(target.Path)) ? target.Client + " does not appear to be installed (client directory missing)" : "";
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
        {
            return ("", "");
        }

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
            if (quote is '"' && command[i] is '\\' && i + 1 < command.Length && command[i + 1] is '$' or '`')
            {
                result.Append(command[i + 1]);
                i += 2;
                continue;
            }
            if (quote is '\'' && command.AsSpan(i).StartsWith("'\\''", StringComparison.Ordinal))
            { result.Append('\''); i += 4; continue; }
            if (command[i] == quote)
            {
                return (result.ToString(), command[(i + 1)..]);
            }

            result.Append(command[i]);
            i++;
        }
        return (result.ToString(), "");
    }
    private static FileChange Edit(string path, string client, string command, bool dryRun, Func<string>? skipMissing = null) => FileEditor.Apply(path, (source, exists) =>
    {
        if ((!exists || string.IsNullOrWhiteSpace(source)) && command.Length is 0)
        {
            return new(source, "absent");
        }
        var output = string.IsNullOrWhiteSpace(source) ? "{}\n" : source;
        HashSet<string> actions = [];
        foreach (var (eventName, subcommand) in client is "claude-code" ? new[] { ("PostToolUse", "event"), ("SessionStart", "session") } : [("PostToolUse", "event")])
        {
            var result = EditEvent(output, client, eventName, subcommand, command.Replace(" hook event ", " hook " + subcommand + " ", StringComparison.Ordinal));
            output = result.Text;
            actions.Add(result.Action);
        }
        var action = actions switch
        {
            _ when actions.Contains("update") => "update",
            _ when actions.Contains("add") && actions.Contains("unchanged") => "update",
            _ when actions.Contains("add") => "add",
            _ when actions.Contains("remove") => "remove",
            _ when actions.Contains("unchanged") => "unchanged",
            _ => "absent",
        };
        return new(output, action);
    }, dryRun, backup: true, skipMissing: skipMissing);
    private static TextEdit EditEvent(string source, string client, string eventName, string subcommand, string wanted)
    {
        var root = JsonConfigurationEditor.ParseObject(source);
        var node = root["hooks"];
        if (root.ContainsKey("hooks") && node is not JsonObject)
        {
            throw new InvalidDataException("hooks is not an object");
        }
        var raw = (node as JsonObject)?[eventName];
        if (raw is not null && raw is not JsonArray)
        {
            throw new InvalidDataException("hooks." + eventName + " is not an array");
        }
        var groups = raw as JsonArray ?? [];
        var current = groups.OfType<JsonObject>().SelectMany(g => g["hooks"] is JsonArray items ? items : []).OfType<JsonObject>().Where(h => Text(h["type"]) is "command" && IsHarness(Text(h["command"]), subcommand)).Select(h => Text(h["command"])).FirstOrDefault() ?? "";
        var rest = WithoutHarness(groups, subcommand);
        if (wanted.Length is 0 && current.Length is 0)
        {
            return new(source, "absent");
        }
        if (wanted.Length > 0 && current == wanted && rest.Count == groups.Count - 1)
        {
            return new(source, "unchanged");
        }
        if (wanted.Length > 0)
        {
            JsonObject item = new() { ["type"] = "command", ["command"] = wanted, ["timeout"] = 10 };
            if (client is "codex")
            {
                item["async"] = true;
            }
            rest.Add(new JsonObject { ["matcher"] = "", ["hooks"] = new JsonArray(item) });
        }
        string output;
        if (node is null)
        {
            output = JsonConfigurationEditor.Set(source, [], "hooks", new JsonObject { [eventName] = rest }).Text;
        }
        else if (rest.Count > 0)
        {
            output = JsonConfigurationEditor.Set(source, ["hooks"], eventName, rest).Text;
        }
        else
        {
            output = JsonConfigurationEditor.Delete(source, ["hooks"], eventName).Text;
        }
        if (JsonConfigurationEditor.ParseObject(output)["hooks"] is JsonObject { Count: 0 })
        {
            output = JsonConfigurationEditor.Delete(output, [], "hooks").Text;
        }
        if (!JsonNode.DeepEquals(Strip(root, eventName, subcommand), Strip(JsonConfigurationEditor.ParseObject(output), eventName, subcommand)))
        {
            throw new InvalidDataException("hook edit would change unrelated configuration; nothing written");
        }
        var action = wanted.Length is 0 ? "remove" : "add";
        return new(output, current.Length > 0 && wanted.Length > 0 ? "update" : action);
    }
    private static JsonObject Strip(JsonObject root, string eventName, string subcommand)
    {
        var copy = (JsonObject)root.DeepClone();
        if (copy["hooks"] is JsonObject hooks)
        {
            var rest = WithoutHarness(hooks[eventName] as JsonArray ?? [], subcommand);
            hooks.Remove(eventName);
            if (rest.Count > 0)
            {
                hooks[eventName] = rest;
            }
            if (hooks.Count is 0)
            {
                copy.Remove("hooks");
            }
        }
        return copy;
    }
    private static JsonArray WithoutHarness(JsonArray groups, string subcommand)
    {
        JsonArray result = [];
        foreach (var group in groups)
        {
            if (group is not JsonObject obj || obj["hooks"] is not JsonArray items)
            { result.Add(group?.DeepClone()); continue; }
            var rest = items.Where(item => item is not JsonObject hook || Text(hook["type"]) is not "command" || !IsHarness(Text(hook["command"]), subcommand)).Select(item => item?.DeepClone()).ToArray();
            if (rest.Length == items.Count)
            {
                result.Add(group.DeepClone());
            }
            else if (rest.Length > 0)
            {
                var copy = (JsonObject)group.DeepClone();
                copy["hooks"] = new JsonArray(rest);
                result.Add(copy);
            }
        }
        return result;
    }
    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
