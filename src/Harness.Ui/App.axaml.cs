using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Harness.Ui.Services;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;

namespace Harness.Ui;

/// <summary>What a host gives the shared UI before Avalonia starts.</summary>
public sealed class UiStartup
{
    public required Api.HarnessApi Api { get; init; }
    public required UiHostContext Host { get; init; }

    /// <summary>
    /// What must happen before the UI talks to its API (the desktop app: find or start harness ui). The window shows
    /// what it reports meanwhile; a <see cref="Hosting.UiServerException"/> is shown in place of the UI.
    /// </summary>
    public Func<IProgress<string>, CancellationToken, Task>? Connect { get; init; }
}

/// <summary>harness ui's Avalonia application: resources, styles and the main view, the same for every host.</summary>
public sealed partial class App : Application
{
    /// <summary>Set by the host before it starts Avalonia.</summary>
    public static UiStartup? Startup { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (Startup is { } startup)
        {
            var host = new ContentControl();
            switch (ApplicationLifetime)
            {
                case ISingleViewApplicationLifetime single:
                    single.MainView = host;
                    break;
                case IClassicDesktopStyleApplicationLifetime desktop:
                    desktop.MainWindow = new Window
                    {
                        Title = "harness",
                        Content = host,
                        Width = 1280,
                        Height = 800,
#pragma warning disable S1075 // Avalonia's assembly resource URI, not a machine-specific path.
                        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Harness.Ui/Assets/logo.png"))),
#pragma warning restore S1075
                    };
                    break;
                default:
                    break;
            }
            _ = ShowAsync(startup, host);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Connects first when the host asks to (showing its progress, or why it failed), then shows the UI.</summary>
    internal static async Task ShowAsync(UiStartup startup, ContentControl host)
    {
        if (startup.Connect is { } connect)
        {
            var status = new StartupView();
            host.Content = status;
            try
            {
                await connect(new Progress<string>(status.Report), CancellationToken.None);
            }
            catch (Hosting.UiServerException exception)
            {
                status.Fail(exception.Message);
                return;
            }
        }
        var model = new MainViewModel(new UiServices(startup.Api, startup.Host));
        host.Content = new MainView { DataContext = model };
        await model.StartAsync(CancellationToken.None);
    }
}
