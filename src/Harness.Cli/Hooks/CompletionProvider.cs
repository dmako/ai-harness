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
        var words = args[1..];
        if (shell is "pwsh" && words.Length > 0)
        {
            words = [.. words[1..], words[0].TrimStart('=')];
        }
        if (words.Length is 0)
        {
            words = [""];
        }
        var current = words[^1];
        Command container = model;
        Command? leaf = null;
        for (var i = 0; i < words.Length - 1; i++)
        {
            var word = words[i];
            if (container.Subcommands.Count > 0)
            {
                if (word.StartsWith('-'))
                {
                    continue;
                }
                var name = word switch
                {
                    "hook" => "hooks",
                    "task" => "tasks",
                    "workspaces" => "workspace",
                    _ => word,
                };
                leaf = container.Subcommands.FirstOrDefault(c => c.Name == name);
                if (leaf is null)
                {
                    return;
                }
                container = leaf;
                continue;
            }
            if (
                i == words.Length - 2
                && word.StartsWith('-')
                && !word.Contains('=', StringComparison.Ordinal)
                && leaf?.Options.Any(p => p.Name == word && p.ValueType != typeof(bool)) is true
            )
            {
                return;
            }
        }
        IEnumerable<(string Name, string Description)> candidates = [];
        if (container.Subcommands.Count > 0 && !current.StartsWith('-'))
        {
            candidates = container.Subcommands.Where(c => !c.Hidden).Select(c => (c.Name, c.Description ?? ""));
        }
        else if (current.StartsWith('-'))
        {
            if (container.Subcommands.Count > 0)
            {
                leaf = container.Subcommands.FirstOrDefault(c => c.Name == Default(leaf?.Name ?? ""));
            }
            if (leaf is not null)
            {
                candidates = leaf
                    .Options.Where(p => !p.Hidden)
                    .Select(p => (p.Name, p.Description ?? ""))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Append(("--help", "show help"));
            }
        }
        foreach (var (name, description) in candidates)
        {
            output.WriteLine(
                shell switch
                {
                    "zsh" => name.Replace(":", "\\:", StringComparison.Ordinal) + ":" + description,
                    "fish" or "pwsh" => name + "\t" + description,
                    _ => name,
                }
            );
        }
        return;
    }

    private static string Default(string group) =>
        group switch
        {
            "hooks" => "choose",
            "completion" => "status",
            "workspace" or "projects" => "list",
            _ => "",
        };
}
