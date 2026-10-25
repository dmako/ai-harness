namespace Harness.Core.Projects;

public interface IHarnessStateSession : IAsyncDisposable
{
    Task<IReadOnlyDictionary<string, object?>> DiagnosticsAsync(CancellationToken cancellationToken);
    Task PutAsync(string key, string value, CancellationToken cancellationToken);
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);
}

public sealed record HarnessRuntime(string Version, string Commit, string Runtime, string Platform);
