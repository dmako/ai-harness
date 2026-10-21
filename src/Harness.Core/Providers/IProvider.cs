namespace Harness.Core.Providers;

public interface IProvider : IAsyncDisposable
{
    string Name { get; }
    // Generic adapters declare capabilities without starting; protocol adapters may handshake lazily.
    IReadOnlyList<CapabilityInfo> Capabilities();
    ValueTask<ProviderHealth> HealthAsync(bool probe, CancellationToken cancellationToken = default);
    Task<ProviderResult> ExecuteAsync(ProviderRequest request, CancellationToken cancellationToken = default);
}

public interface IProviderRefresher : IProvider
{
    Task<RefreshResult> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default);
}

public interface IVersionedProvider
{
    string ServerVersion { get; }
}

public sealed class ProviderException(string provider, string message, string tool = "", string hint = "", Exception? innerException = null)
    : Exception(Format(provider, message, tool, hint), innerException)
{
    public ProviderException() : this("", "provider failure") { }
    public ProviderException(string message) : this("", message) { }
    public ProviderException(string message, Exception innerException) : this("", message, innerException: innerException) { }
    public string Provider { get; } = provider;
    public string Detail { get; } = message;
    public string Tool { get; } = tool;
    public string Hint { get; } = hint;
    private static string Format(string provider, string message, string tool, string hint) =>
        $"{provider}{(tool.Length is 0 ? "" : $" ({tool})")}: {message}{(hint.Length is 0 ? "" : " — " + hint)}";
}

public sealed class UnsupportedCapabilityException : Exception
{
    public UnsupportedCapabilityException() : base("capability not supported by provider") { }
    public UnsupportedCapabilityException(string message) : base(message) { }
    public UnsupportedCapabilityException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ProviderNotIndexedException : Exception
{
    public ProviderNotIndexedException() : base("not indexed by the provider") { }
    public ProviderNotIndexedException(string message) : base(message) { }
    public ProviderNotIndexedException(string message, Exception innerException) : base(message, innerException) { }
}
