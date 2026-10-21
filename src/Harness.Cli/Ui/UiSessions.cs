using Harness.Core.Graphify;
using Harness.Infrastructure.Configuration;
using Harness.Infrastructure.Graphify;
using Harness.Infrastructure.Projects;

namespace Harness.Cli.Ui;

/// <summary>Shares only the bounded session-list cache; session commands never block project reads.</summary>
internal sealed class UiSessions(ProjectLifecycle lifecycle) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private GraphifySessionManager? _manager;
    private string _command = "";
    private int _generation;
    private int _observedGeneration;
    public void Forget() => Interlocked.Increment(ref _generation);
    public async Task<IReadOnlyList<GraphifySession>> ListAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var generation = Volatile.Read(ref _generation);
            var configuration = ConfigurationLoader.LoadFiles(lifecycle.GlobalConfigurationPath, "").Value;
            var (command, watch) = GraphifyDiscovery.FromConfig(configuration.Providers);
            if (_manager is null || _command != command || _observedGeneration != generation)
            {
                if (_manager is not null)
                { await _manager.DisposeAsync(); }
                var directory = Path.GetDirectoryName(Path.GetFullPath(lifecycle.GlobalConfigurationPath))!;
                _manager = new(new GraphifyOptions(directory, command) { WatchArguments = watch, RecordsPath = Path.Combine(directory, "graphify-sessions.json") });
                _command = command;
                _observedGeneration = generation;
            }
            return await _manager.ListAsync(true, token);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_manager is not null)
        { await _manager.DisposeAsync(); }
        _gate.Dispose();
    }
}
