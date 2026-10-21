using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Core.Providers;
using Tomlyn;
using Tomlyn.Model;

namespace Harness.Infrastructure.Telemetry;

/// <summary>Read command identities only; never start a client or provider.</summary>
public static class ProviderServerMap
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Variable = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public static IReadOnlyDictionary<string, string> Read(string root, IReadOnlyDictionary<string, ProviderConfiguration> providers, string? claudeFile = null, string? codexFile = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        claudeFile ??= Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : home, ".claude.json");
        codexFile ??= Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codex ? codex : Path.Combine(home, ".codex"), "config.toml");
        List<(string Name, string Command)> servers = [];
        void Add(Dictionary<string, Server?>? table)
        {
            if (table is not null)
            {
                servers.AddRange(table.Select(pair => (pair.Key, pair.Value?.Command ?? "")));
            }
        }
        ClientConfiguration? ReadJson(string path)
        {
            try
            { return JsonSerializer.Deserialize<ClientConfiguration>(ReadText(path), JsonOptions); }
            catch (Exception error) when (Optional(error)) { return null; }
        }
        Add(ReadJson(Path.Combine(root, ".mcp.json"))?.McpServers);
        var user = ReadJson(claudeFile);
        Add(user?.Projects?.GetValueOrDefault(root)?.McpServers);
        Add(user?.McpServers);
        try
        {
            var node = JsonSerializer.SerializeToNode(TomlSerializer.Deserialize<TomlTable>(ReadText(codexFile)));
            Add(node?["mcp_servers"]?.Deserialize<Dictionary<string, Server?>>(JsonOptions));
        }
        catch (Exception error) when (Optional(error)) { /* Individual malformed client files are optional. */ }
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (var server in servers.Where(s => s.Name is not "harness"))
        {
            foreach (var (name, config) in providers.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (server.Name == name || server.Name == name + "-mcp" || config.Command.Length > 0 && Path.GetFileName(Expand(server.Command)) == Path.GetFileName(Expand(config.Command)))
                { result[server.Name] = name; break; }
            }
        }

        return result;
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record Server([property: System.Text.Json.Serialization.JsonPropertyName("command")] string? Command);
    private sealed record ClientConfiguration(
        [property: System.Text.Json.Serialization.JsonPropertyName("mcpServers")] Dictionary<string, Server?>? McpServers,
        [property: System.Text.Json.Serialization.JsonPropertyName("projects")] Dictionary<string, ClientConfiguration?>? Projects);
    private static string ReadText(string path)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("client configuration exceeds the read limit");
        }

        return File.ReadAllText(path, Utf8);
    }
    private static string Expand(string command)
    {
        var expanded = Variable.Replace(command, match => Environment.GetEnvironmentVariable(match.Groups[1].Value) is { Length: > 0 } value ? value : match.Groups[2].Value);
        return expanded.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]) : expanded;
    }
    private static bool Optional(Exception e) => e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or TomlException or InvalidOperationException or ArgumentException;
}
