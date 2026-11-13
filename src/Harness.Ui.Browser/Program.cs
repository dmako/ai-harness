using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Harness.Ui;
using Harness.Ui.Api;
using Harness.Ui.ViewModels;

[assembly: SupportedOSPlatform("browser")]

namespace Harness.Ui.Browser;

/// <summary>
/// Starts the shared UI in the page's #out element. The API is this page's origin; a deep link
/// (/next/#/p/&lt;project id&gt;/board) chooses the project and follows the project shown.
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
                Navigated = id => Location.SetHash($"#/p/{Uri.EscapeDataString(id)}/board"),
                BoardShown = () => Location.Mark("board"),
                LiveReloaded = () => Location.Count("liveReloads"),
            },
            ProjectId = ProjectFromHash(location.Fragment),
        };
        return AppBuilder.Configure<App>().WithInterFont().StartBrowserAppAsync("out");
    }

    /// <summary>The project id of a hash like #/p/&lt;id&gt;/board, else null.</summary>
    private static string? ProjectFromHash(string hash)
    {
        var parts = hash.TrimStart('#', '/').Split('/');
        return parts is ["p", { Length: > 0 } id, ..] ? Uri.UnescapeDataString(id) : null;
    }
}

/// <summary>The page's address (globalThis.location), through main.js's exports.</summary>
internal static partial class Location
{
    [JSImport("globalThis.harnessLocation.href")]
    internal static partial string Href();

    [JSImport("globalThis.harnessLocation.setHash")]
    internal static partial void SetHash(string hash);

    [JSImport("globalThis.harnessLocation.mark")]
    internal static partial void Mark(string name);

    [JSImport("globalThis.harnessLocation.count")]
    internal static partial void Count(string name);
}
