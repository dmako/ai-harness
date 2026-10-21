using AwesomeAssertions;
using Harness.Core.Tasks;
using Xunit;

namespace Harness.Unit.Tasks;

public sealed class TaskWorkflowTests
{
    [Fact]
    public void TransitionMatrixMatchesFrozenWorkflowIncludingReviewAndDeferred()
    {
        Dictionary<string, string[]> expected = new(StringComparer.Ordinal)
        {
            ["backlog"] = ["ready", "blocked", "deferred"],
            ["ready"] = ["claimed", "backlog", "blocked", "deferred"],
            ["claimed"] = ["in_progress", "review", "ready", "done", "blocked"],
            ["in_progress"] = ["review", "ready", "done", "blocked"],
            ["review"] = ["in_progress", "done", "blocked"],
            ["blocked"] = ["backlog", "ready", "deferred"],
            ["deferred"] = ["backlog", "ready"],
            ["done"] = [],
        };
        foreach (var from in TaskWorkflow.Statuses)
        {
            foreach (var to in TaskWorkflow.Statuses)
            {
                TaskWorkflow.CanTransition(from, to).Should().Be(expected[from].Contains(to, StringComparer.Ordinal));
            }
        }

        TaskWorkflow.ParseStatus(" IN_PROGRESS ").Should().Be("in_progress");
        TaskWorkflow.HoldsClaim("deferred").Should().BeFalse();
        TaskWorkflow.HoldsClaim("review").Should().BeTrue();
    }
}
