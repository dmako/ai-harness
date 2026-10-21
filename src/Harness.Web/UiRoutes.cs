namespace Harness.Web;

public static class UiRoutes
{
    public static IReadOnlyList<string> Writes { get; } =
    [
        "/api/projects/{id}/memories/{mid}/supersede", "/api/projects/{id}/memories/{mid}/retire", "/api/projects/{id}/memories/{mid}/promote",
        "/api/knowledge/memories", "/api/knowledge/memories/{mid}/supersede", "/api/knowledge/memories/{mid}/retire",
        "/api/projects/{id}/proposals/{pid}/approve", "/api/projects/{id}/proposals/{pid}/reject",
        "/api/projects/{id}/findings/{action}", "/api/workspaces/{name}/findings/{action}",
        "/api/projects/{id}/tasks", "/api/projects/{id}/task-proposals/{pid}/approve", "/api/projects/{id}/task-proposals/{pid}/reject",
        "/api/projects/{id}/arch-proposals/{pid}/approve", "/api/projects/{id}/arch-proposals/{pid}/reject",
        "/api/graphify/sessions/{action}", "/api/workspaces", "/api/workspaces/{name}/join", "/api/workspaces/{name}/leave",
        "/api/workspaces/{name}/delete", "/api/workspaces/{name}/rename", "/api/projects/{id}/rename",
    ];
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
