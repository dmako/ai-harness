using System.Diagnostics.CodeAnalysis;

namespace Harness.Scm;

/// <summary>The SCM server software a configured connection talks to.</summary>
public enum ScmKind
{
    /// <summary>GitLab, REST API v4.</summary>
    Gitlab,

    /// <summary>Forgejo (and Gitea-compatible servers), REST API v1.</summary>
    Forgejo,
}

/// <summary>The lower-case names of <see cref="ScmKind" /> used in configuration, CLI and JSON.</summary>
public static class ScmKinds
{
    public static IReadOnlyList<string> Names { get; } = ["gitlab", "forgejo"];

    public static string Name(ScmKind kind) =>
        kind switch
        {
            ScmKind.Gitlab => "gitlab",
            ScmKind.Forgejo => "forgejo",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SCM kind."),
        };

    public static string Display(ScmKind kind) =>
        kind switch
        {
            ScmKind.Gitlab => "GitLab",
            ScmKind.Forgejo => "Forgejo",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown SCM kind."),
        };

    public static bool TryParse(string? text, [NotNullWhen(true)] out ScmKind? kind)
    {
        kind = text?.Trim().ToUpperInvariant() switch
        {
            "GITLAB" => ScmKind.Gitlab,
            "FORGEJO" => ScmKind.Forgejo,
            _ => null,
        };
        return kind is not null;
    }
}
