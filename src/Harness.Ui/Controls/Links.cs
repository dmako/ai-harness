using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Harness.Ui.Controls;

/// <summary>
/// What a canvas page lacks of a browser's links (TASK-0144): every HyperlinkButton gets a context menu with Open in
/// new tab and Copy link, and a middle click opens it in a new tab (Links.Menu, set for all by Styles/Buttons.axaml).
/// Button.copychip with Links.Copy copies its text and shows that it did. Opening goes through
/// <see cref="Opener"/>, the host's launcher (a new tab in the browser), which tests replace.
/// </summary>
public static class Links
{
    public static readonly AttachedProperty<bool> MenuProperty = AvaloniaProperty.RegisterAttached<
        HyperlinkButton,
        bool
    >("Menu", typeof(Links));

    public static readonly AttachedProperty<string?> CopyProperty = AvaloniaProperty.RegisterAttached<
        Button,
        string?
    >("Copy", typeof(Links));

    static Links()
    {
        MenuProperty.Changed.AddClassHandler<HyperlinkButton>((link, e) =>
        {
            if (e.NewValue is true)
            {
                link.ContextMenu = Menu(link);
                link.AddHandler(InputElement.PointerPressedEvent, MiddleClick, RoutingStrategies.Tunnel);
            }
        });
        CopyProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            button.Click -= CopyClick;
            if (e.NewValue is string)
            {
                button.Click += CopyClick;
            }
        });
    }

    /// <summary>Opens a link in a new tab (the browser) or the default browser (desktop); tests record it.</summary>
    public static Func<TopLevel?, Uri, Task> Opener { get; set; } =
        async (top, uri) =>
        {
            if (top?.Launcher is { } launcher)
            {
                await launcher.LaunchUriAsync(uri);
            }
        };

    public static bool GetMenu(HyperlinkButton link) => link.GetValue(MenuProperty);

    public static void SetMenu(HyperlinkButton link, bool value) => link.SetValue(MenuProperty, value);

    public static string? GetCopy(Button button) => button.GetValue(CopyProperty);

    public static void SetCopy(Button button, string? value) => button.SetValue(CopyProperty, value);

    /// <summary>Puts <paramref name="text"/> on the clipboard of <paramref name="from"/>'s window.</summary>
    public static async Task CopyAsync(Visual from, string text)
    {
        if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private static ContextMenu Menu(HyperlinkButton link) => MenuFor(link, () => link.NavigateUri);

    /// <summary>Open in new tab and Copy link for any control that stands for <paramref name="uri"/>.</summary>
    public static ContextMenu MenuFor(Control owner, Func<Uri?> uri)
    {
        var open = new MenuItem { Header = "Open in new tab" };
        open.Click += async (_, _) =>
        {
            if (uri() is { } target)
            {
                await Opener(TopLevel.GetTopLevel(owner), target);
            }
        };
        var copy = new MenuItem { Header = "Copy link" };
        copy.Click += async (_, _) =>
        {
            if (uri() is { } target)
            {
                await CopyAsync(owner, target.ToString());
            }
        };
        return new ContextMenu { Items = { open, copy } };
    }

    /// <summary>A middle click on <paramref name="owner"/> opens <paramref name="uri"/> in a new tab.</summary>
    public static void MiddleClickOpens(Control owner, Func<Uri?> uri)
    {
        ArgumentNullException.ThrowIfNull(owner);
        owner.AddHandler(
            InputElement.PointerPressedEvent,
            async (_, e) =>
            {
                if (
                    e.GetCurrentPoint(owner).Properties.PointerUpdateKind is PointerUpdateKind.MiddleButtonPressed
                    && uri() is { } target
                )
                {
                    e.Handled = true;
                    await Opener(TopLevel.GetTopLevel(owner), target);
                }
            },
            RoutingStrategies.Tunnel
        );
    }

    private static async void MiddleClick(object? sender, PointerPressedEventArgs e)
    {
        if (
            sender is HyperlinkButton { NavigateUri: { } uri } link
            && e.GetCurrentPoint(link).Properties.PointerUpdateKind is PointerUpdateKind.MiddleButtonPressed
        )
        {
            e.Handled = true;
            await Opener(TopLevel.GetTopLevel(link), uri);
        }
    }

    private static async void CopyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { } button || GetCopy(button) is not { } text)
        {
            return;
        }
        await CopyAsync(button, text);
        button.Classes.Add("copied");
        DispatcherTimer.RunOnce(() => button.Classes.Remove("copied"), TimeSpan.FromMilliseconds(1600));
    }
}
