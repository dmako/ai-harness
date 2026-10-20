using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console.Cli;

namespace Harness.Cli;

internal sealed class HostTypeRegistrar(HostApplicationBuilder builder) : ITypeRegistrar, IDisposable, IAsyncDisposable
{
    private IHost? _host;

    public ITypeResolver Build()
    {
        _host ??= builder.Build();
        return new Resolver(_host.Services);
    }

    public void Register(Type service, Type implementation) => builder.Services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => builder.Services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => builder.Services.AddSingleton(service, _ => factory());

    public void Dispose() => _host?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync();
        }
        else
        {
            _host?.Dispose();
        }
    }

    private sealed class Resolver(IServiceProvider services) : ITypeResolver
    {
        public object? Resolve(Type? type) => type is null ? null : services.GetService(type);
    }
}
