using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Harness.Ui.ViewModels;
using Harness.Ui.Views;

namespace Harness.Ui;

/// <summary>What a host gives the shared UI before Avalonia starts.</summary>
public sealed class UiStartup
{
    public required Api.HarnessApi Api { get; init; }
    public required UiHostContext Host { get; init; }

    /// <summary>The project to show first (a deep link), else the first listed.</summary>
    public string? ProjectId { get; init; }
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
            var model = new MainViewModel(startup.Api, startup.Host);
            var view = new MainView { DataContext = model };
            switch (ApplicationLifetime)
            {
                case ISingleViewApplicationLifetime single:
                    single.MainView = view;
                    break;
                case IClassicDesktopStyleApplicationLifetime desktop:
                    desktop.MainWindow = new Window { Title = "harness", Content = view, Width = 1280, Height = 800 };
                    break;
                default:
                    break;
            }
            _ = model.StartAsync(startup.ProjectId, CancellationToken.None);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
