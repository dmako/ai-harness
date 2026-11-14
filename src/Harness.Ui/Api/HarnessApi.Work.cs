namespace Harness.Ui.Api;

/// <summary>The work area: blueprints, tasks and their trail and sessions, and the proposals waiting.</summary>
public sealed partial class HarnessApi
{
    public Task<BlueprintList> BlueprintsAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "blueprints"), UiJsonContext.Default.BlueprintList, token);

    public Task<NextId> NextTaskIdAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "tasks-next-id"), UiJsonContext.Default.NextId, token);

    public Task<NextId> NextBlueprintIdAsync(string projectId, CancellationToken token) =>
        GetAsync(Project(projectId, "blueprints-next-id"), UiJsonContext.Default.NextId, token);

    public Task<TaskEvents> TaskEventsAsync(string projectId, string taskId, CancellationToken token) =>
        GetAsync(Project(projectId, $"tasks/{Uri.EscapeDataString(taskId)}/events"), UiJsonContext.Default.TaskEvents, token);

    public Task<TaskSessions> TaskSessionsAsync(string projectId, string taskId, CancellationToken token) =>
        GetAsync(Project(projectId, $"tasks/{Uri.EscapeDataString(taskId)}/sessions"), UiJsonContext.Default.TaskSessions, token);

    public Task<TaskProposalList> TaskProposalsAsync(string projectId, string status, CancellationToken token) =>
        GetAsync(Project(projectId, $"task-proposals?status={Uri.EscapeDataString(status)}"), UiJsonContext.Default.TaskProposalList, token);

    public Task<BlueprintProposalList> BlueprintProposalsAsync(string projectId, string status, CancellationToken token) =>
        GetAsync(
            Project(projectId, $"blueprint-proposals?status={Uri.EscapeDataString(status)}"),
            UiJsonContext.Default.BlueprintProposalList,
            token
        );

    public Task<ArchProposalList> ArchProposalsAsync(string projectId, string status, CancellationToken token) =>
        GetAsync(Project(projectId, $"arch-proposals?status={Uri.EscapeDataString(status)}"), UiJsonContext.Default.ArchProposalList, token);

    public Task CreateTaskAsync(string projectId, TaskForm task, CancellationToken token) =>
        PostAsync(Project(projectId, "tasks"), task, UiJsonContext.Default.TaskForm, token);

    public Task EditTaskAsync(string projectId, string taskId, TaskForm task, CancellationToken token) =>
        PostAsync(Project(projectId, $"tasks/{Uri.EscapeDataString(taskId)}/edit"), task, UiJsonContext.Default.TaskForm, token);

    public Task DeleteTaskAsync(string projectId, string taskId, CancellationToken token) =>
        PostAsync(Project(projectId, $"tasks/{Uri.EscapeDataString(taskId)}/delete"), Nothing, UiJsonContext.Default.DictionaryStringString, token);

    public Task MoveTaskAsync(string projectId, string taskId, string blueprint, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"tasks/{Uri.EscapeDataString(taskId)}/blueprint"),
            new TaskMove { Blueprint = blueprint },
            UiJsonContext.Default.TaskMove,
            token
        );

    public Task CreateBlueprintAsync(string projectId, BlueprintForm blueprint, CancellationToken token) =>
        PostAsync(Project(projectId, "blueprints"), blueprint, UiJsonContext.Default.BlueprintForm, token);

    public Task EditBlueprintAsync(string projectId, string blueprintId, BlueprintForm blueprint, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"blueprints/{Uri.EscapeDataString(blueprintId)}/edit"),
            blueprint,
            UiJsonContext.Default.BlueprintForm,
            token
        );

    public Task RemoveBlueprintAsync(string projectId, string blueprintId, BlueprintRemoval removal, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"blueprints/{Uri.EscapeDataString(blueprintId)}/remove"),
            removal,
            UiJsonContext.Default.BlueprintRemoval,
            token
        );

    public Task OrderBlueprintsAsync(string projectId, IReadOnlyList<string> order, CancellationToken token) =>
        PostAsync(Project(projectId, "blueprints-order"), new BlueprintOrder { Order = order }, UiJsonContext.Default.BlueprintOrder, token);

    public Task ApproveTaskAsync(string projectId, string proposalId, TaskForm task, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"task-proposals/{Uri.EscapeDataString(proposalId)}/approve"),
            new TaskApproval { Task = task },
            UiJsonContext.Default.TaskApproval,
            token
        );

    public Task ApproveBlueprintAsync(string projectId, string proposalId, BlueprintApproval approval, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"blueprint-proposals/{Uri.EscapeDataString(proposalId)}/approve"),
            approval,
            UiJsonContext.Default.BlueprintApproval,
            token
        );

    public Task ApproveArchAsync(string projectId, string proposalId, ArchApproval approval, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"arch-proposals/{Uri.EscapeDataString(proposalId)}/approve"),
            approval,
            UiJsonContext.Default.ArchApproval,
            token
        );

    /// <summary>Rejects a proposal; <paramref name="kind"/> is task, blueprint or arch.</summary>
    public Task RejectAsync(string projectId, string kind, string proposalId, string note, CancellationToken token) =>
        PostAsync(
            Project(projectId, $"{kind}-proposals/{Uri.EscapeDataString(proposalId)}/reject"),
            new Rejection { Note = note },
            UiJsonContext.Default.Rejection,
            token
        );

    /// <summary>The body of a write with nothing to say: {}.</summary>
    private static readonly Dictionary<string, string> Nothing = [];

    private static string Project(string projectId, string path) => $"api/projects/{Uri.EscapeDataString(projectId)}/{path}";
}
