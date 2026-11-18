using Avalonia;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args) =>
        AppBuilder.Configure<SpikeApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
}
