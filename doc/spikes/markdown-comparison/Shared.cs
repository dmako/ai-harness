using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

public sealed class SpikeApp : Application
{
    private const string Sample =
        "# Heading 👩‍💻\n\n**Bold** and [link](https://example.com).\n\n| A | B |\n|---|---|\n| one | two |\n\n- [x] done\n\n```csharp\nvar x = 1;\n```";

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
#if live
        var content = new LiveMarkdown.Avalonia.MarkdownRenderer
        {
            MarkdownBuilder = new LiveMarkdown.Avalonia.ObservableStringBuilder(Sample),
        };
#elif markview
        var content = new MarkView.Avalonia.MarkdownViewer { Markdown = Sample };
#else
        var content = new Harness.Ui.Controls.MarkdownBlock { Source = Sample };
#endif
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window
            {
                Width = 900,
                Height = 700,
                Content = content,
            };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime browser)
            browser.MainView = content;
        base.OnFrameworkInitializationCompleted();
    }
}
