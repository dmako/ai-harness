namespace Harness.Core.Providers;

public sealed record ProviderProcessOptions(string Command, IReadOnlyList<string> Arguments)
{
    public string WorkingDirectory { get; init; } = "";
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>Owns redirected pipes and the child's OS containment group, including its descendants.</summary>
public interface IProviderProcess : IAsyncDisposable
{
    int Id { get; }
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    Task<int> Completion { get; }
    void KillTree();
}

public interface IProviderProcessFactory
{
    IProviderProcess Start(ProviderProcessOptions options);
}
