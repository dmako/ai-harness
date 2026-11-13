using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Threading;
using Harness.Ui;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.ViewModels;

[assembly: SupportedOSPlatform("browser")]

namespace Harness.Ui.Browser;

/// <summary>
/// Starts the shared UI in the page's #out element. The API is this page's origin; the page's address is the shell's
/// history (<see cref="BrowserNavigation"/>), so links are shareable and Back/Forward work.
/// </summary>
internal static partial class Program
{
    /// <summary>The browser HttpClient handler's switch for a streamed response body.</summary>
    private static readonly HttpRequestOptionsKey<bool> StreamingResponse = new("WebAssemblyEnableStreamingResponse");

    /// <summary>The page's one client, alive as long as the page.</summary>
    private static readonly HttpClient Http = new();

    private static Task Main()
    {
        var location = new Uri(Location.Href());
        var server = new Uri(new Uri(location.GetLeftPart(UriPartial.Authority)), "/");
        Http.BaseAddress = server;
        App.Startup = new UiStartup
        {
            Api = new HarnessApi(Http)
            {
                // /api/events never ends: its body must stream through fetch's ReadableStream, not be buffered
                Streaming = request => request.Options.Set(StreamingResponse, true),
            },
            Host = new UiHostContext
            {
                Server = server,
                Page = new Uri(location.GetLeftPart(UriPartial.Path)),
                Navigation = BrowserNavigation.Instance,
                BoardShown = () => Location.Mark("board"),
                LiveReloaded = () => Location.Count("liveReloads"),
            },
        };
        return AppBuilder.Configure<App>().WithInterFont().StartBrowserAppAsync("out");
    }
}

/// <summary>
/// The shell's history in the address bar: going somewhere pushes the hash (history.pushState), and the browser's
/// Back/Forward or an edited address come back as hash changes (main.js calls <see cref="HashChanged"/>).
/// </summary>
internal sealed partial class BrowserNavigation : INavigation
{
    private BrowserNavigation() => Current = Route.Parse(Location.Hash());

    public static BrowserNavigation Instance { get; } = new();

    public Route Current { get; private set; }

    public event EventHandler? Changed;

    public void Go(string href)
    {
        var route = Route.Parse(href);
        if (route.Href != Current.Href)
        {
            Location.Push(route.Href);
        }
        Show(route);
    }

    public void Replace(string href)
    {
        var route = Route.Parse(href);
        Location.Replace(route.Href);
        Show(route);
    }

    public void Back() => Location.Back();

    public void Forward() => Location.Forward();

    /// <summary>The address's hash changed by the browser (Back, Forward, an edited address).</summary>
    [JSExport]
    internal static void HashChanged(string hash) =>
        Dispatcher.UIThread.Post(() =>
        {
            var route = Route.Parse(hash);
            if (route.Href != Instance.Current.Href || route.Moved)
            {
                Instance.Show(route);
            }
        });

    private void Show(Route route)
    {
        Current = route;
        Changed?.Invoke(this, EventArgs.Empty);
        // the page shown, for tests and measurements (window.harnessTimings.route)
        Location.Shown(route.Href);
    }
}

/// <summary>The page's address and history (globalThis.harnessLocation in main.js).</summary>
internal static partial class Location
{
    [JSImport("globalThis.harnessLocation.href")]
    internal static partial string Href();

    [JSImport("globalThis.harnessLocation.hash")]
    internal static partial string Hash();

    [JSImport("globalThis.harnessLocation.push")]
    internal static partial void Push(string hash);

    [JSImport("globalThis.harnessLocation.replace")]
    internal static partial void Replace(string hash);

    [JSImport("globalThis.harnessLocation.back")]
    internal static partial void Back();

    [JSImport("globalThis.harnessLocation.forward")]
    internal static partial void Forward();

    [JSImport("globalThis.harnessLocation.shown")]
    internal static partial void Shown(string href);

    [JSImport("globalThis.harnessLocation.mark")]
    internal static partial void Mark(string name);

    [JSImport("globalThis.harnessLocation.count")]
    internal static partial void Count(string name);
}
