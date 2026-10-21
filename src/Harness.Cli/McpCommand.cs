using System.Diagnostics.CodeAnalysis;
using Harness.Core.Projects;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Graphify;
using Harness.Providers.Registry;
using Harness.Mcp;
using Harness.Infrastructure.Processes;
using Harness.Providers.Packs;
using Harness.Infrastructure.Providers;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Harness.Cli;

internal sealed class McpCommand(ProjectLifecycle lifecycle, BuildInformation information, ILoggerFactory loggerFactory, IServiceProvider services) : AsyncCommand<ProjectSettings>
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "HarnessMcpServer.RunAsync takes ownership of the session and asynchronously disposes it after stopping all requests.")]
    public override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(settings.Project))
        {
            throw new DirectoryNotFoundException("Project directory does not exist or is not a directory: " + Path.GetFullPath(settings.Project));
        }

        var location = await lifecycle.LocateAsync(settings.Project, allowNonGit: true, cancellationToken);
        var languages = ProviderRouting.DetectLanguages(location.Root);
        if (location.Configuration.Value.Providers.Values.Any(provider => provider.Pack is "graphify-csharp" && provider.IsEnabled()) && !languages.Contains("csharp", StringComparer.Ordinal))
        {
            var candidate = GraphifyDiscovery.FindInput(location.Root);
            if (candidate.AtRoot || candidate.Path.Length > 0 || candidate.Candidates is { Count: > 0 })
                languages = [.. languages, "csharp"];
        }
        await using var providers = ConfiguredProviders.Build(location.Configuration.Value.Providers, location.Root, new ProviderProcessFactory(), detectedLanguages: languages);
        await using var calls = new ProviderCallRecorder(location.DatabasePath, location.Configuration.Value.Storage.BusyTimeoutMs);
        providers.SetObserver(calls.Observe);
        var runtime = new HarnessRuntime(information.Version, information.Commit, information.Runtime, information.Platform);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var session = new HarnessStateSession(location, lifecycle.Registry, runtime, lifecycle.GlobalConfigurationPath);
        try
        {
            await HarnessMcpServer.RunAsync(input, output, session, runtime, loggerFactory, services, providers, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupt and stdin EOF are ordinary stdio server shutdowns, as in Go.
        }

        return 0;
    }
}
