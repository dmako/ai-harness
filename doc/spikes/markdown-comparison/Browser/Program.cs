using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;

[assembly: SupportedOSPlatform("browser")]

internal static class Program
{
    private static Task Main() => AppBuilder.Configure<SpikeApp>().WithInterFont().StartBrowserAppAsync("out");
}
