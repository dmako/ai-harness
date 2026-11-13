using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Harness.Ui.Api;
using Harness.Ui.Navigation;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

/// <summary>The shared App on Avalonia's headless platform, drawn with Skia so frames can be captured.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Runs tests on the headless UI thread and opens views against <see cref="FixtureApi"/>. See README.md for why
/// tests start the session themselves and dispatch as they do.
/// </summary>
internal static class Ui
{
    public static readonly Uri Server = new("http://127.0.0.1:17477/");

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(
        () => HeadlessUnitTestSession.StartNew(typeof(TestApp))
    );

    /// <summary>
    /// Runs <paramref name="test"/> to its end on the UI thread. Only the Func&lt;Task&lt;T&gt;&gt; overload of Dispatch
    /// keeps the dispatcher running until the task ends; an async lambda as Func&lt;Task&gt; binds to the Func&lt;T&gt;
    /// one, which ends the test at its first await and loses its failures.
    /// </summary>
    public static Task RunAsync(Func<Task> test) =>
        Session.Value.Dispatch(
            async () =>
            {
                await test();
                return true;
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>
    /// The main view in a shown window of <paramref name="width"/> x <paramref name="height"/>, its Board loaded from
    /// <paramref name="api"/> (a new FixtureApi, its Board answer <paramref name="board"/> when given).
    /// </summary>
    public static async Task<(Window Window, FixtureApi Api, MainViewModel Model)> OpenMainAsync(
        int width,
        int height,
        string? board = null,
        FixtureApi? api = null,
        bool fastLive = false,
        string? address = null,
        IPreferences? preferences = null
    )
    {
        api ??= new FixtureApi();
        if (board is not null)
        {
            api.Answers[$"/api/projects/{Fixtures.ProjectId}/board"] = board;
        }
        // the client lives as long as the window: closing it disposes the client and the view model
#pragma warning disable CA2000 // disposed by the window's Closed handler
        var http = new HttpClient(api) { BaseAddress = Server };
#pragma warning restore CA2000
#pragma warning disable CA2000 // disposed by the window's Closed handler
        var services = Services(http, fastLive, new InMemoryNavigation(address ?? $"#/p/{Fixtures.ProjectId}/board"), preferences);
#pragma warning restore CA2000
        var model = new MainViewModel(services);
        var window = Show(new MainView { DataContext = model }, width, height);
        window.Closed += (_, _) =>
        {
            model.Dispose();
            services.Dispose();
            http.Dispose();
        };
        await model.StartAsync(TestContext.Current.CancellationToken);
        await UntilAsync(() =>
            model.Page is not null
            && (model.Page is not ProjectPage { View: "board" } || model.Board is { Loading: false, Columns.Count: > 0 })
        );
        return (window, api, model);
    }

    /// <summary>
    /// The page's services over <paramref name="http"/> as a desktop host has them (writes carry the Origin). With
    /// <paramref name="fastLive"/> live updates use test timings: 100 ms quiet, 600 ms gap, 200 ms reconnect.
    /// </summary>
    public static UiServices Services(
        HttpClient http,
        bool fastLive = false,
        Harness.Ui.Navigation.INavigation? navigation = null,
        IPreferences? preferences = null
    )
    {
        var api = new HarnessApi(http) { Origin = Server.GetLeftPart(UriPartial.Authority) };
        var host = new UiHostContext
        {
            Server = Server,
            Navigation = navigation ?? new InMemoryNavigation(),
            Preferences = preferences ?? new InMemoryPreferences(),
        };
        if (!fastLive)
        {
            return new UiServices(api, host);
        }
        UiServices? services = null;
#pragma warning disable CA2000 // owned and disposed by the UiServices
        var updates = new LiveUpdates(
            () => services?.Dialogs.IsOpen is true,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(600),
            TimeSpan.FromMilliseconds(200)
        );
#pragma warning restore CA2000
        services = new UiServices(api, host, updates);
        return services;
    }

    /// <summary><paramref name="content"/> in a shown window of the given size, laid out.</summary>
    public static Window Show(Control content, int width, int height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = content,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Runs the UI thread's jobs and render ticks until <paramref name="condition"/> holds, at most 5 s.</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the view did not get there in 5 s");
            }
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
