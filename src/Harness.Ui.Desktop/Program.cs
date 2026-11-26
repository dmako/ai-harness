using Avalonia;
using Harness.Ui;
using Harness.Ui.Api;
using Harness.Ui.Hosting;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var address = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("HARNESS_UI_ADDR");
        address = string.IsNullOrWhiteSpace(address) ? "127.0.0.1:17477" : address;
        var server = new Uri(address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address);
        using var http = new HttpClient { BaseAddress = server };
        // a harness ui already on the address is used; else the app starts one there and stops it when it closes
        using var handler = new SocketsHttpHandler();
        using var ui = new UiServer(handler, new ServerProcessStarter());
        App.Startup = new UiStartup
        {
            Api = new HarnessApi(http) { Origin = server.GetLeftPart(UriPartial.Authority) },
            Host = new UiHostContext { Server = server },
            Connect = (status, token) => ui.ConnectAsync(server, status, token),
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}
