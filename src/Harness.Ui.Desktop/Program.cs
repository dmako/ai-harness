using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Harness.Ui;
using Harness.Ui.Api;
using Harness.Ui.Hosting;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Desktop;

internal static class Program
{
    /// <summary>The release smoke check: start, report how far it got on stdout, and exit (0 once the UI loaded).</summary>
    private const string SmokeFlag = "--smoke";

    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(90);

    [STAThread]
    private static int Main(string[] args)
    {
        var smoke = args.Contains(SmokeFlag, StringComparer.Ordinal);
        args = [.. args.Where(a => a != SmokeFlag)];
        var address = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("HARNESS_UI_ADDR");
        address = string.IsNullOrWhiteSpace(address) ? "127.0.0.1:17477" : address;
        var server = new Uri(address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address);
        using var http = new HttpClient { BaseAddress = server };
        // a harness ui already on the address is used; else the app starts one there and stops it when it closes
        using var handler = new SocketsHttpHandler();
        using var ui = new UiServer(handler, new ServerProcessStarter());
        using var deadline = smoke ? new Timer(_ => Exit("timeout", 4), null, SmokeTimeout, Timeout.InfiniteTimeSpan) : null;
        App.Startup = new UiStartup
        {
            Api = new HarnessApi(http) { Origin = server.GetLeftPart(UriPartial.Authority) },
            Host = new UiHostContext { Server = server },
            Connect = (status, token) => ui.ConnectAsync(server, status, token),
            Reached = smoke ? Smoke : null,
        };
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);

        void Exit(string reached, int code)
        {
            Console.Out.WriteLine("harness-ui-app: " + reached);
            Console.Out.Flush();
            ui.Stop();
            Environment.Exit(code);
        }
    }

    private static void Smoke(string reached)
    {
        Console.Out.WriteLine("harness-ui-app: " + reached);
        Console.Out.Flush();
        if (reached is "window")
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(reached is "ui" ? 0 : 3)
        );
    }
}
