using Spectre.Console.Cli.Help;
using Spectre.Console.Rendering;

namespace Harness.Cli.Hooks;

/// <summary>Completes the installed command model instead of keeping a second command tree.</summary>
internal sealed class CompletionProvider(string[] args, TextWriter output) : IHelpProvider
{
    public IEnumerable<IRenderable> Write(ICommandModel model, ICommandInfo? command)
    {
        if (args.Length is 0)
        {
            return [];
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
        ICommandContainer container = model;
        ICommandInfo? leaf = null;
        for (var i = 0; i < words.Length - 1; i++)
        {
            var word = words[i];
            if (container.Commands.Count > 0)
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
                leaf = container.Commands.FirstOrDefault(c => c.Name == name);
                if (leaf is null)
                {
                    return [];
                }
                container = leaf;
                continue;
            }
            if (
                i == words.Length - 2
                && word.StartsWith('-')
                && !word.Contains('=', StringComparison.Ordinal)
                && leaf
                    ?.Parameters.OfType<ICommandOption>()
                    .Any(p => p.LongNames.Contains(word.TrimStart('-'), StringComparer.Ordinal) && !p.IsFlag)
                    is true
            )
            {
                return [];
            }
        }
        IEnumerable<(string Name, string Description)> candidates = [];
        if (container.Commands.Count > 0 && !current.StartsWith('-'))
        {
            candidates = container.Commands.Where(c => !c.IsHidden).Select(c => (c.Name, c.Description ?? ""));
        }
        else if (current.StartsWith('-'))
        {
            if (container.Commands.Count > 0)
            {
                leaf = container.Commands.FirstOrDefault(c => c.Name == Default(leaf?.Name ?? ""));
            }
            if (leaf is not null)
            {
                candidates = leaf
                    .Parameters.OfType<ICommandOption>()
                    .Where(p => !p.IsHidden)
                    .SelectMany(p => p.LongNames.Select(n => ("--" + n, p.Description ?? "")))
                    .OrderBy(p => p.Item1, StringComparer.Ordinal)
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
        return [];
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
