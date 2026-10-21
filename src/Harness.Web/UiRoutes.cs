namespace Harness.Web;

public static class UiRoutes
{
    public static IReadOnlyList<string> Reads { get; } =
    [
        "/api/projects", "/api/projects/{id}/summary", "/api/projects/{id}/snippet",
        "/api/projects/{id}/context", "/api/projects/{id}/metrics", "/api/projects/{id}/memories",
        "/api/projects/{id}/tasks", "/api/projects/{id}/proposals", "/api/projects/{id}/milestones",
        "/api/projects/{id}/tasks/{tid}/events", "/api/projects/{id}/health", "/api/projects/{id}/architecture",
        "/api/projects/{id}/coupling", "/api/projects/{id}/structure", "/api/projects/{id}/report",
        "/api/projects/{id}/providers", "/api/projects/{id}/hooks", "/api/projects/{id}/memories/{mid}",
        "/api/projects/{id}/applies", "/api/projects/{id}/tasks-next-id", "/api/projects/{id}/task-proposals",
        "/api/projects/{id}/arch-proposals", "/api/projects/{id}/stack", "/api/knowledge",
        "/api/knowledge/memories/{mid}", "/api/machine", "/api/graphify/sessions", "/api/search",
        "/api/activity", "/api/workspaces", "/api/workspaces/discover", "/api/workspaces/{name}/snippet",
        "/api/workspaces/{name}/dependencies", "/api/workspaces/{name}/adoption", "/api/workspaces/{name}/duplicates",
        "/api/workspaces/{name}/issues", "/api/workspaces/{name}/system", "/api/workspaces/{name}/findings",
        "/api/workspaces/{name}/stack", "/api/stacks",
    ];
}
