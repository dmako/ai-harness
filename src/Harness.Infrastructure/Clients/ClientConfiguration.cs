using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Core.Instructions;

namespace Harness.Infrastructure.Clients;

public sealed record ServerSpec(string Command, IReadOnlyList<string> Args);
public sealed record ClientTarget(string Client, string Scope, string Path, IReadOnlyList<string> Keys);
public sealed record ClientEntry(string Scope, string ConfigPath, string Command, IReadOnlyList<string> Args, string Resolved, string Problem);
public sealed record ClientStatus(string Client, IReadOnlyList<ClientEntry> Entries, IReadOnlyList<string> Checked, IReadOnlyList<string> Errors)
{
    public bool Configured => Entries.Count > 0;
}
public sealed record ClientPaths(string Home, string ClaudeFile, string CodexFile)
{
    public static ClientPaths Current()
    {
        var homeKey = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
        var home = Environment.GetEnvironmentVariable(homeKey) is { Length: > 0 } value ? value : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(home, Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claude ? claude : home, ".claude.json"), Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codex ? codex : Path.Combine(home, ".codex"), "config.toml"));
    }
}
public interface IAgentClient
{
    string Id { get; }
    string Name { get; }
    IReadOnlyList<string> Scopes { get; }
    string ConfigPath { get; }
    IReadOnlyList<ClientTarget> Targets(string scope, string root);
    TextEdit Edit(string source, bool exists, ClientTarget target, ServerSpec? spec);
    string Missing(ClientTarget target);
}
public sealed class ClientConfiguration(ClientPaths paths)
{
    private static readonly Regex Variables = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public IReadOnlyList<IAgentClient> All { get; } = [new ClaudeClient(paths), new CodexClient(paths)];
    public IAgentClient Get(string id) => All.FirstOrDefault(c => c.Id == id) ?? throw new ArgumentException("unknown client \"" + id + "\" (want claude-code, codex or all)");
    public IReadOnlyList<ClientTarget> Targets(string client, string scope, string root)
    {
        if (scope is not ("user" or "project" or "local" or "all"))
            throw new ArgumentException("unknown scope \"" + scope + "\" (want user, project or local)");
        return (client is "all" ? All : [Get(client)]).SelectMany(c => c.Targets(scope, root)).ToArray();
    }
    public FileChange Install(ClientTarget target, ServerSpec spec, bool dryRun = false) => Apply(target, spec, dryRun);
    public FileChange Uninstall(ClientTarget target, bool dryRun = false) => Apply(target, null, dryRun);
    private FileChange Apply(ClientTarget target, ServerSpec? spec, bool dryRun)
    {
        var client = Get(target.Client);
        return FileEditor.Apply(target.Path, (source, exists) => !exists && spec is null ? new(source, "absent") : client.Edit(source, exists, target, spec), dryRun, backup: true, skipMissing: () => client.Missing(target));
    }
    public string Expand(string command) => Expand(command, paths.Home);
    private static string Expand(string command, string home)
    {
        var expanded = Variables.Replace(command, match => Environment.GetEnvironmentVariable(match.Groups[1].Value) is { Length: > 0 } value ? value : match.Groups[2].Value);
        return expanded.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, expanded[2..]) : expanded;
    }
    public ClientStatus Detect(IAgentClient client, string root)
    {
        List<ClientEntry> entries = [];
        List<string> checkedPaths = [];
        List<string> errors = [];
        foreach (var group in client.Targets("all", root).GroupBy(t => t.Path, StringComparer.Ordinal))
        {
            checkedPaths.Add(group.Key);
            try
            {
                if (!File.Exists(group.Key))
                    continue;
                var text = FileEditor.Read(group.Key);
                var node = client.Id is "codex" ? System.Text.Json.JsonSerializer.SerializeToNode(TomlConfigurationEditor.Parse(text))!.AsObject() : JsonConfigurationEditor.ParseObject(text);
                foreach (var target in group)
                {
                    JsonNode? current = node;
                    foreach (var key in client.Id is "codex" ? new[] { "mcp_servers" } : target.Keys)
                        current = (current as JsonObject)?[key];
                    if ((current as JsonObject)?["harness"] is not JsonObject entry)
                        continue;
                    var command = entry["command"]?.GetValue<string>() ?? "";
                    var args = (entry["args"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToArray() ?? [];
                    var resolved = Expand(command);
                    var problem = resolved.Length is 0 ? "no command configured" : "";
                    if (Path.IsPathRooted(resolved) && !Executable(resolved))
                        problem = "command " + resolved + " not found or not executable";
                    entries.Add(new(target.Scope, target.Path, command, args, resolved, problem));
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or Tomlyn.TomlException or ArgumentException) { errors.Add(group.Key + ": " + error.Message); }
        }
        return new(client.Id, entries, checkedPaths, errors);
    }
    public static bool Executable(string path) => File.Exists(path) && (OperatingSystem.IsWindows() ? Path.GetExtension(path).ToUpperInvariant() is ".EXE" or ".COM" or ".BAT" or ".CMD" : (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) is not 0);
    internal static JsonNode? Pop(JsonObject root, IReadOnlyList<string> keys)
    {
        if (keys.Count is 0)
        {
            var value = root["harness"]?.DeepClone();
            root.Remove("harness");
            return value;
        }
        if (root[keys[0]] is not JsonObject child)
            return null;
        var result = Pop(child, keys.Skip(1).ToArray());
        if (child.Count is 0)
            root.Remove(keys[0]);
        return result;
    }
    private sealed class ClaudeClient(ClientPaths paths) : IAgentClient
    {
        public string Id => "claude-code";
        public string Name => "Claude Code";
        public IReadOnlyList<string> Scopes => ["user", "project", "local"];
        public string ConfigPath => paths.ClaudeFile;
        public IReadOnlyList<ClientTarget> Targets(string scope, string root)
        {
            List<ClientTarget> targets = [];
            if (scope is "project" or "all")
                targets.Add(new(Id, "project", Path.Combine(root, ".mcp.json"), ["mcpServers"]));
            if (scope is "local" or "all")
                targets.Add(new(Id, "local", ConfigPath, ["projects", root, "mcpServers"]));
            if (scope is "user" or "all")
                targets.Add(new(Id, "user", ConfigPath, ["mcpServers"]));
            return targets;
        }
        public string Missing(ClientTarget target) => target.Scope is "project" ? "" : "Claude Code user config " + target.Path + " not found (run Claude Code once, or use --scope project)";
        public TextEdit Edit(string source, bool exists, ClientTarget target, ServerSpec? spec)
        {
            if (spec is not null && (!exists || string.IsNullOrWhiteSpace(source)))
                source = "{}\n";
            var before = JsonConfigurationEditor.ParseObject(source);
            JsonNode? current = before;
            foreach (var key in target.Keys)
                current = (current as JsonObject)?[key];
            var hasExisting = current is JsonObject container && container.ContainsKey("harness");
            var existing = (current as JsonObject)?["harness"];
            TextEdit edit;
            JsonObject? want = null;
            if (spec is null)
                edit = JsonConfigurationEditor.Delete(source, target.Keys, "harness");
            else
            {
                if (hasExisting && existing is not JsonObject)
                    throw new InvalidDataException("existing harness entry is not an object");
                want = existing?.DeepClone() as JsonObject ?? new();
                if (Expand(want["command"]?.GetValue<string>() ?? "", paths.Home) == Expand(spec.Command, paths.Home) && (!want.ContainsKey("type") || want["type"]?.GetValue<string>() is "stdio") && JsonNode.DeepEquals(want["args"], System.Text.Json.JsonSerializer.SerializeToNode(spec.Args)))
                    return new(source, "unchanged");
                want["type"] = "stdio";
                want["command"] = spec.Command;
                want["args"] = System.Text.Json.JsonSerializer.SerializeToNode(spec.Args);
                edit = JsonConfigurationEditor.Set(source, target.Keys, "harness", want);
            }
            var after = JsonConfigurationEditor.ParseObject(edit.Text);
            var got = Pop(after, target.Keys);
            Pop(before, target.Keys);
            if (!JsonNode.DeepEquals(want, got) || !JsonNode.DeepEquals(before, after))
                throw new InvalidDataException("edit would change unrelated configuration; nothing written");
            return edit;
        }
    }
    private sealed class CodexClient(ClientPaths paths) : IAgentClient
    {
        public string Id => "codex";
        public string Name => "Codex";
        public IReadOnlyList<string> Scopes => ["user"];
        public string ConfigPath => paths.CodexFile;
        public IReadOnlyList<ClientTarget> Targets(string scope, string root) => [new(Id, "user", ConfigPath, [])];
        public TextEdit Edit(string source, bool exists, ClientTarget target, ServerSpec? spec) => TomlConfigurationEditor.Edit(source, spec);
        public string Missing(ClientTarget target) => Directory.Exists(Path.GetDirectoryName(target.Path)) ? "" : "Codex does not appear to be installed (" + Path.GetDirectoryName(target.Path) + " missing)";
    }
}
