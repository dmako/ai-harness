using Harness.Core.Instructions;

namespace Harness.Infrastructure.Clients;

public static class ProjectInstructions
{
    public static FileChange Ensure(string path, bool dryRun = false) =>
        FileEditor.Apply(path, (source, _) => AgentGuidance.Block.Put(source, AgentGuidance.Section), dryRun);

    public static FileChange Remove(string path, bool dryRun = false) =>
        FileEditor.Apply(
            path,
            (source, exists) => exists ? AgentGuidance.Block.Cut(source) : new(source, "absent"),
            dryRun
        );

    public static FileChange EnsureImport(string path, bool dryRun = false) =>
        FileEditor.Apply(
            path,
            (source, _) =>
                AgentGuidance.ImportsItself(source)
                    ? AgentGuidance.Block.Cut(source)
                    : AgentGuidance.Block.Put(source, AgentGuidance.ImportBlock),
            dryRun
        );

    public static IReadOnlyList<FileChange> Project(string root, bool codex, bool dryRun = false)
    {
        var agents = Path.Combine(root, AgentGuidance.FileName);
        var claude = Path.Combine(root, AgentGuidance.ClaudeFileName);
        if (File.Exists(claude) && !File.Exists(agents) && !codex)
        {
            return [Ensure(claude, dryRun)];
        }
        // Validate both before either is written, including a malformed existing import block.
        Ensure(agents, true);
        EnsureImport(claude, true);
        return [Ensure(agents, dryRun), EnsureImport(claude, dryRun)];
    }

    /// <summary>
    /// The harness blocks already in the project's AGENTS.md and CLAUDE.md restated as this harness writes them
    /// (the guidance section, or CLAUDE.md's import of AGENTS.md); a file without one is left alone, none is added.
    /// </summary>
    public static IReadOnlyList<FileChange> Refresh(string root, bool dryRun = false)
    {
        List<FileChange> changes = [];
        foreach (var name in new[] { AgentGuidance.FileName, AgentGuidance.ClaudeFileName })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path))
            {
                continue;
            }

            var source = FileEditor.Read(path);
            var (start, end) = AgentGuidance.Block.Find(source);
            if (start < 0)
            {
                continue;
            }

            var imports = source[start..end].Split('\n').Any(line => line.Trim() is "@AGENTS.md" or "@./AGENTS.md");
            var block = imports ? AgentGuidance.ImportBlock : AgentGuidance.Section;
            changes.Add(FileEditor.Apply(path, (text, _) => AgentGuidance.Block.Put(text, block), dryRun));
        }

        return changes;
    }

    public static IReadOnlyList<FileChange> RemoveProject(string root, bool dryRun = false)
    {
        var paths = new[]
        {
            Path.Combine(root, AgentGuidance.FileName),
            Path.Combine(root, AgentGuidance.ClaudeFileName),
        };
        foreach (var path in paths)
        {
            Remove(path, true);
        }

        return paths.Select(path => Remove(path, dryRun)).ToArray();
    }

    public static (bool Section, bool Reaches) ClaudeReaches(string root)
    {
        string Read(string name) =>
            File.Exists(Path.Combine(root, name)) ? FileEditor.Read(Path.Combine(root, name)) : "";
        var claude = Read(AgentGuidance.ClaudeFileName);
        var agents = Read(AgentGuidance.FileName);
        if (
            claude.Contains(AgentGuidance.Marker, StringComparison.Ordinal)
            && claude
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Contains(AgentGuidance.Body.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal)
        )
        {
            return (true, true);
        }

        return agents.Contains(AgentGuidance.Marker, StringComparison.Ordinal)
            ? (
                true,
                claude
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Contains(AgentGuidance.ImportBlock, StringComparison.Ordinal)
                    || AgentGuidance.ImportsItself(claude)
            )
            : (false, false);
    }
}
