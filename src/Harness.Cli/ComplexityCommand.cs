using Harness.Cli.Memory;
using Harness.Infrastructure.Architecture.Metrics;
using Harness.Infrastructure.Projects;

namespace Harness.Cli;

internal sealed class ComplexityMeasureSettings : ProjectSettings
{
    public string Commit { get; init; } = "";
}

/// <summary>The detached worker 'harness sync' starts: measures one commit with Microsoft.CodeAnalysis.Metrics.</summary>
internal sealed class ComplexityMeasureCommand(ProjectLifecycle lifecycle, TextWriter output, CliErrorOutput error)
{
    public async Task<int> ExecuteAsync(ComplexityMeasureSettings settings, CancellationToken cancellationToken)
    {
        if (!MetricsStore.ValidCommit(settings.Commit))
        {
            await error.Writer.WriteLineAsync("harness complexity measure: --commit must be a commit id");
            return 2;
        }

        var worker = new MetricsWorker(
            lifecycle,
            new MetricsExecutable(MetricsStore.ToolCache(lifecycle.GlobalConfigurationPath))
        );
        var run = await worker.RunAsync(settings.Project, settings.Commit, cancellationToken);
        await output.WriteLineAsync(
            run.State switch
            {
                "done" =>
                    $"measured {run.Measured.Count} project(s), kept {run.Reused.Count}: {run.Members.Count} members",
                "failed" => $"failed ({run.Failure}): {run.Error}",
                _ => run.State,
            }
        );
        return run.State is "failed" ? 1 : 0;
    }
}
