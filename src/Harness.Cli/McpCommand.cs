using System.Diagnostics.CodeAnalysis;
using Harness.Core.Projects;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Processes;
using Harness.Infrastructure.Projects;
using Harness.Infrastructure.Providers;
using Harness.Infrastructure.Telemetry;
using Harness.Infrastructure.Workspaces;
using Harness.Mcp;
using Harness.Mcp.Workspaces;
using Harness.Providers.Packs;
using Harness.Providers.Registry;
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
        var languages = Languages(location.Root, location.Configuration.Value.Providers);
        await using var providers = ConfiguredProviders.Build(location.Configuration.Value.Providers, location.Root, new ProviderProcessFactory(), detectedLanguages: languages);
        await using var calls = new TelemetryRecorder(location.DatabasePath, location.Configuration.Value.Storage.BusyTimeoutMs, name => providers.Providers().OfType<Core.Providers.IVersionedProvider>().FirstOrDefault(p => p is Core.Providers.IProvider provider && provider.Name == name)?.ServerVersion ?? "");
        providers.SetObserver(calls.Observe);
        var runtime = new HarnessRuntime(information.Version, information.Commit, information.Runtime, information.Platform);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var session = new HarnessStateSession(location, lifecycle.Registry, runtime, lifecycle.GlobalConfigurationPath) { ContextObserver = calls };
        await using var workspace = new WorkspaceTools(session, membership => new WorkspaceSearchSession(membership, providers, (member, deep) =>
        {
            var configuration = ConfigurationLoader.LoadFiles(lifecycle.GlobalConfigurationPath, ConfigurationPaths.ProjectPath(member.Root));
            return ConfiguredProviders.Build(configuration.Value.Providers, member.Root, new ProviderProcessFactory(), light: !deep, detectedLanguages: deep ? Languages(member.Root, configuration.Value.Providers) : null);
        }), providers, session);
        try
        {
            await HarnessMcpServer.RunAsync(input, output, session, runtime, loggerFactory, services, providers, workspace, calls, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupt and stdin EOF are ordinary stdio server shutdowns, as in Go.
        }

        return 0;
    }
    private static IReadOnlyList<string> Languages(string root, IReadOnlyDictionary<string, Harness.Core.Providers.ProviderConfiguration> configuration)
    {
        var languages = ProviderRouting.DetectLanguages(root);
        if (configuration.Values.Any(provider => provider.Pack is "graphify-csharp" && provider.IsEnabled()) && !languages.Contains("csharp", StringComparer.Ordinal))
        {
            var candidate = GraphifyDiscovery.FindInput(root);
            if (candidate.AtRoot || candidate.Path.Length > 0 || candidate.Candidates is { Count: > 0 })
            {
                return [.. languages, "csharp"];
            }
        }
        return languages;
    }
}
