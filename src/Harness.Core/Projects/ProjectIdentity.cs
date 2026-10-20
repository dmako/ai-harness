namespace Harness.Core.Projects;

public sealed record ProjectIdentity(string Id, string Root, string DatabasePath, string CopiedFrom, string CreatedAt);

public sealed record WorktreeIdentity(string Id, string Path, string GitDir, bool Linked, string Branch, string Commit, bool Detached);

public sealed record ProjectRegistration(ProjectIdentity Project, WorktreeIdentity Worktree, bool Created, bool Moved, bool Copied, string PreviousRoot);
