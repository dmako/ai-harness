using System.Text;

namespace Harness.Infrastructure.Configuration;

/// <summary>One option as config.toml shows it: commented out, with its default or a sample value.</summary>
public sealed record ExampleKey(string Name, string Value, string Note = "");

/// <summary>A table of options as config.toml shows it; "providers.&lt;name&gt;" stands for any provider.</summary>
public sealed record ExampleTable(string Name, string Note, IReadOnlyList<ExampleKey> Keys);

/// <summary>
/// Every configuration option, as the commented examples 'harness init' writes to the project and global
/// config.toml, and the tables and keys harness accepts, by which an older file is brought up to date.
/// </summary>
public static partial class ConfigurationExample
{
    private const string Mapping = "providers.<name>.capabilities.\"code.symbol_search\"";

    public static IReadOnlyList<ExampleTable> Tables { get; } =
    [
        new(
            "storage",
            "",
            [
                new("db_path", "\".harness/harness.db\"", "relative to the main working tree"),
                new("busy_timeout_ms", "5000", "how long a write waits for another one"),
            ]
        ),
        new("log", "", [new("level", "\"info\"", "debug | info | warn | error")]),
        new("sync", "", [new("ignore", "[\"vendor/\", \"*.min.js\"]", "on top of .gitignore")]),
        new(
            "architecture.complexity",
            "cyclomatic complexity of functions",
            [
                new("languages", "[\"csharp\", \"go\"]", "add \"typescript\" for TypeScript/JavaScript"),
                new("note", "11", "lowest value reported as a note"),
                new("warn", "21", "... as a warning"),
                new("problem", "51", "... as a problem"),
            ]
        ),
        new(
            "architecture.complexity.typescript",
            "one language's own thresholds (also csharp, go)",
            [new("note", "11"), new("warn", "31"), new("problem", "51")]
        ),
        new(
            "architecture.complexity.metrics",
            "exact .NET values (Windows, .NET SDK)",
            [
                new("enabled", "false", "true: sync starts a background run per HEAD"),
                new("timeout_ms", "600000", "a longer run fails and keeps the counted values"),
            ]
        ),
        new(
            "scm",
            "this project's SCM when its git remotes do not match one ('harness scm' configures them)",
            [
                new("name", "\"work\"", "an SCM from 'harness scm list'"),
                new("project", "\"group/sub/repo\"", "its path on that SCM (owner/repo on Forgejo)"),
                new("remote", "\"origin\"", "match only this remote"),
            ]
        ),
        new(
            "providers.codebase-memory",
            "symbol and text search, overview (any language)",
            [new("command", "\"/path/to/codebase-memory-mcp\"")]
        ),
        new(
            "providers.ripwire",
            "ranked symbol and text search, name-based callers",
            [new("command", "\"/path/to/ripwire\"")]
        ),
        new("providers.rg-fd", "text and file search", [new("command", "\"/path/to/rg-fd-mcp\"")]),
        new(
            "providers.graphify-csharp",
            "compiler-exact C# callers, references, impact",
            [
                new("command", "\"/path/to/harness-graphify\""),
                new("args", "[\"--graphify\", \"~/.dotnet/tools/graphify-csharp.exe\"]"),
            ]
        ),
        new(
            "providers.nuget",
            "package_api for C# packages",
            [
                new("command", "\"dnx\""),
                new(
                    "args",
                    "[\"DimonSmart.NugetMcpServer@1.1.10\", \"--yes\", \"--source\", \"https://api.nuget.org/v3/index.json\"]"
                ),
            ]
        ),
        new(
            "providers.<name>",
            "any provider: every key",
            [
                new("pack", "\"ripwire\"", "start from a built-in mapping"),
                new("type", "\"mcp\"", "mcp, or harness for a built-in one"),
                new("enabled", "true", "false switches it off"),
                new("command", "\"/path/to/server\"", "the executable, or a name on PATH"),
                new("args", "[\"--mcp\"]"),
                new("timeout_ms", "10000", "per call; 0 keeps the default"),
                new("only", "[\"code.symbol_search\"]", "use just these capabilities"),
                new("languages", "[\"csharp\"]", "answer for these languages only"),
                new("context", "true", "false keeps it out of get_context"),
                new("dir", "\"project\"", "start it in the project root"),
                new("freshness", "\"indexed\"", "live | indexed"),
                new("index_worktrees", "false", "an index per worktree (needs refresh and resolve)"),
            ]
        ),
        new(
            Mapping,
            "a capability mapped to one of its tools",
            [
                new("tool", "\"search\""),
                new("args", "{ query = \"{query}\", limit = \"{limit}\" }"),
                new("items", "\"results\"", "where the result list is"),
                new("fields", "{ name = \"symbol\", path = \"file\", start_line = \"line\" }"),
                new("evidence", "\"exact\""),
                new("kind", "\"\""),
                new("where", "{}"),
                new("error", "\"error\""),
                new("truncated", "\"has_more\""),
                new("pattern", "''", "a regex over a text answer"),
                new("miss", "\"not found\"", "an answer meaning no results"),
                new("chain", "[]"),
                new("combine", "\"first\"", "first | all"),
            ]
        ),
        new(
            "providers.<name>.refresh",
            "brings its index up to date on sync",
            [new("tool", "\"index\""), new("args", "{ path = \"{root}\" }"), new("skip", "''")]
        ),
        new(
            "providers.<name>.resolve",
            "finds the index of a worktree",
            [
                new("tool", "\"list_projects\""),
                new("args", "{}"),
                new("items", "\"projects\""),
                new("match", "\"root_path\""),
                new("value", "\"name\""),
                new("fix", "\"\""),
                new("index", "[\"$INDEX_DIR\", \"~/.cache/indexes\"]", "where its indexes are; a change asks again"),
            ]
        ),
        new(
            "pricing.<model>",
            "a model's API price, US$ per million tokens (Claude models are built in)",
            [
                new("input", "1.25"),
                new("cached_input", "0.125", "left out: the built-in price, else the input price"),
                new("cache_write", "1.25"),
                new("output", "10.0"),
            ]
        ),
    ];

    /// <summary>
    /// The tables harness accepts, by pattern ("*" any one name), with their keys; null keys: any key (a tool's
    /// arguments). A table matching none, or a key its table does not have, is no option of this harness.
    /// </summary>
    internal static IReadOnlyList<(string Pattern, IReadOnlyList<string>? Keys)> Schema { get; } =
    [
        ("", ["storage", "log", "sync", "architecture", "providers", "pricing", "scm"]),
        ("scm", ["name", "project", "remote"]),
        ("pricing", null),
        ("pricing.*", ["input", "cached_input", "cache_write", "output"]),
        ("storage", ["db_path", "busy_timeout_ms"]),
        ("log", ["level"]),
        ("sync", ["ignore"]),
        ("architecture", ["complexity"]),
        ("architecture.complexity", ["languages", "note", "warn", "problem", "csharp", "go", "typescript", "metrics"]),
        ("architecture.complexity.csharp", Levels),
        ("architecture.complexity.go", Levels),
        ("architecture.complexity.typescript", Levels),
        ("architecture.complexity.metrics", ["enabled", "timeout_ms"]),
        ("providers", null),
        (
            "providers.*",
            [
                "type",
                "pack",
                "enabled",
                "command",
                "args",
                "timeout_ms",
                "only",
                "languages",
                "context",
                "dir",
                "freshness",
                "capabilities",
                "refresh",
                "resolve",
                "index_worktrees",
            ]
        ),
        ("providers.*.capabilities", null),
        ("providers.*.capabilities.*", MappingKeys),
        ("providers.*.capabilities.*.chain", MappingKeys),
        ("providers.*.capabilities.*.args", null),
        ("providers.*.capabilities.*.fields", null),
        ("providers.*.capabilities.*.where", null),
        ("providers.*.capabilities.*.chain.args", null),
        ("providers.*.capabilities.*.chain.fields", null),
        ("providers.*.capabilities.*.chain.where", null),
        ("providers.*.refresh", ["tool", "args", "skip"]),
        ("providers.*.refresh.args", null),
        ("providers.*.resolve", ["tool", "args", "items", "match", "value", "fix", "index"]),
        ("providers.*.resolve.args", null),
    ];

    private static string[] Levels => ["note", "warn", "problem"];

    private static string[] MappingKeys =>
        [
            "tool",
            "args",
            "items",
            "fields",
            "evidence",
            "kind",
            "where",
            "error",
            "truncated",
            "pattern",
            "miss",
            "chain",
            "combine",
        ];

    /// <summary>
    /// The options ("table.key") and tables ("[table]") earlier harness versions had and this one dropped: init
    /// removes their settings from config.toml. Add an option here when removing it from the configuration.
    /// </summary>
    public static IReadOnlyCollection<string> Dropped { get; } = [];

    /// <summary>A new config.toml: the header and every option, commented out.</summary>
    public static string Text(string header)
    {
        ArgumentNullException.ThrowIfNull(header);
        var text = new StringBuilder(header);
        foreach (var table in Tables)
        {
            text.Append('\n');
            foreach (var line in Lines(table, table.Keys))
            {
                text.Append(line).Append('\n');
            }
        }

        return text.ToString();
    }

    public const string ProjectHeader =
        "# Harness project configuration. Values here override the global config\n"
        + "# (see 'harness docs configuration'). Everything is optional; uncomment to change\n"
        + "# a default. 'harness init' adds options new harness versions bring and removes\n"
        + "# the ones they drop; your other lines stay as they are.\n";

    public const string GlobalHeader =
        "# Harness configuration for every project on this machine; a project's\n"
        + "# .harness/config.toml overrides it (see 'harness docs configuration'). Providers\n"
        + "# belong here: their paths are this machine's. Everything is optional; uncomment\n"
        + "# to change a default. 'harness init' adds options new harness versions bring and\n"
        + "# removes the ones they drop; your other lines stay as they are.\n";

    private static IEnumerable<string> Lines(ExampleTable table, IEnumerable<ExampleKey> keys)
    {
        yield return Commented("[" + table.Name + "]", table.Note);
        foreach (var key in keys)
        {
            yield return Commented(key.Name + " = " + key.Value, key.Note);
        }
    }

    private static string Commented(string text, string note) =>
        note.Length is 0 ? "# " + text : ("# " + text).PadRight(36) + " # " + note;
}
