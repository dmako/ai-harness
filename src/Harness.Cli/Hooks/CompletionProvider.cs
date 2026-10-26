using System.CommandLine;

namespace Harness.Cli.Hooks;

/// <summary>Completes the installed command model instead of keeping a second command tree.</summary>
internal static class CompletionProvider
{
    public static void Write(RootCommand model, string[] args, TextWriter output)
    {
        if (args.Length is 0)
        {
            return;
        }
        var shell = args[0];
        var words = Words(shell, args[1..]);
        if (Resolve(model, words) is not { } resolved)
        {
            return;
        }

        foreach (var (name, description) in Candidates(resolved.Container, resolved.Leaf, words[^1]))
        {
            output.WriteLine(Format(shell, name, description));
        }
    }

    /// <summary>The words typed, the one being completed last; PowerShell passes that one first, after '='.</summary>
    private static string[] Words(string shell, string[] words)
    {
        if (shell is "pwsh" && words.Length > 0)
        {
            words = [.. words[1..], words[0].TrimStart('=')];
        }

        return words.Length is 0 ? [""] : words;
    }

    /// <summary>
    /// The command group the completed word belongs to and the last command named; null for an unknown
    /// command, or when the word is the value of the option before it.
    /// </summary>
    private static (Command Container, Command? Leaf)? Resolve(RootCommand model, string[] words)
    {
        Command container = model;
        Command? leaf = null;
        for (var i = 0; i < words.Length - 1; i++)
        {
            var word = words[i];
            if (container.Subcommands.Count is 0)
            {
                if (i == words.Length - 2 && TakesValue(leaf, word))
                {
                    return null;
                }

                continue;
            }

            if (word.StartsWith('-'))
            {
                continue;
            }

            leaf = container.Subcommands.FirstOrDefault(c => c.Name == Alias(word));
            if (leaf is null)
            {
                return null;
            }
            container = leaf;
        }
        return (container, leaf);
    }

    private static string Alias(string word) =>
        word switch
        {
            "hook" => "hooks",
            "task" => "tasks",
            "workspaces" => "workspace",
            _ => word,
        };

    /// <summary>A non-boolean option of the command, written without '=value'.</summary>
    private static bool TakesValue(Command? leaf, string word) =>
        word.StartsWith('-')
        && !word.Contains('=', StringComparison.Ordinal)
        && leaf?.Options.Any(p => p.Name == word && p.ValueType != typeof(bool)) is true;

    /// <summary>
    /// The group's visible subcommands, or for an option prefix the options of the command (a group's
    /// default command) and --help.
    /// </summary>
    private static IEnumerable<(string Name, string Description)> Candidates(
        Command container,
        Command? leaf,
        string current
    )
    {
        if (!current.StartsWith('-'))
        {
            return container.Subcommands.Count > 0
                ? container.Subcommands.Where(c => !c.Hidden).Select(c => (c.Name, c.Description ?? ""))
                : [];
        }

        if (container.Subcommands.Count > 0)
        {
            leaf = container.Subcommands.FirstOrDefault(c => c.Name == Default(leaf?.Name ?? ""));
        }

        return leaf is null
            ? []
            : leaf
                .Options.Where(p => !p.Hidden)
                .Select(p => (p.Name, p.Description ?? ""))
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Append(("--help", "show help"));
    }

    private static string Format(string shell, string name, string description) =>
        shell switch
        {
            "zsh" => name.Replace(":", "\\:", StringComparison.Ordinal) + ":" + description,
            "fish" or "pwsh" => name + "\t" + description,
            _ => name,
        };

    private static string Default(string group) =>
        group switch
        {
            "hooks" => "choose",
            "completion" => "status",
            "workspace" or "projects" => "list",
            _ => "",
        };
}
