using System.Globalization;

namespace Harness.Ui.Api;

/// <summary>The overview area: a project's tasks, report and trends, and the activity feed.</summary>
public sealed partial class HarnessApi
{
    public Task<TaskList> TasksAsync(string projectId, CancellationToken token) =>
        GetAsync($"api/projects/{Uri.EscapeDataString(projectId)}/tasks", UiJsonContext.Default.TaskList, token);

    public Task<ProjectReport> ReportAsync(string projectId, int days, CancellationToken token) =>
        GetAsync(
            $"api/projects/{Uri.EscapeDataString(projectId)}/report?days={days.ToString(CultureInfo.InvariantCulture)}",
            UiJsonContext.Default.ProjectReport,
            token
        );

    public Task<ProjectTrends> TrendsAsync(string projectId, int days, CancellationToken token) =>
        GetAsync(
            $"api/projects/{Uri.EscapeDataString(projectId)}/trends?days={days.ToString(CultureInfo.InvariantCulture)}",
            UiJsonContext.Default.ProjectTrends,
            token
        );

    /// <summary>The last <paramref name="days"/> days' activity, one project's or the machine's (null).</summary>
    public Task<ActivityFeed> ActivityAsync(string? projectId, int days, int limit, CancellationToken token) =>
        GetAsync(
            string.Create(CultureInfo.InvariantCulture, $"api/activity?days={days}&limit={limit}")
                + (projectId is null ? "" : "&project=" + Uri.EscapeDataString(projectId)),
            UiJsonContext.Default.ActivityFeed,
            token
        );
}
