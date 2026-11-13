using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Harness.Ui.Controls;

/// <summary>
/// A link inside the page (a sidebar row, a card, "See Health"): a click goes to <see cref="Href"/> in place through
/// the shell's history (it raises <see cref="NavigateEvent"/>), while Open in new tab, Copy link and a middle click
/// use its full address on the page (<see cref="Resolve"/>, set by the shell). It looks like a Button; style classes
/// make it a sidebar row (nav) or a text link (link).
/// </summary>
public sealed class AppLink : Button
{
    public static readonly StyledProperty<string?> HrefProperty = AvaloniaProperty.Register<AppLink, string?>(nameof(Href));

    public static readonly RoutedEvent<NavigateEventArgs> NavigateEvent = RoutedEvent.Register<AppLink, NavigateEventArgs>(
        "Navigate",
        RoutingStrategies.Bubble
    );

    public AppLink()
    {
        ContextMenu = Links.MenuFor(this, () => Address);
        Links.MiddleClickOpens(this, () => Address);
        // on Click (not OnClick): a raised Click (keyboard, automation, tests) follows the link too
        Click += (_, _) =>
        {
            if (Href is { } href)
            {
                RaiseEvent(new NavigateEventArgs(NavigateEvent, href));
            }
        };
    }

    /// <summary>Turns a hash address into the page's full address; the shell sets it for its host.</summary>
    public static Func<string, Uri?> Resolve { get; set; } = _ => null;

    /// <summary>The hash address it goes to, e.g. #/p/&lt;id&gt;/board.</summary>
    public string? Href
    {
        get => GetValue(HrefProperty);
        set => SetValue(HrefProperty, value);
    }

    /// <summary>Its full address, for opening in a new tab and copying.</summary>
    public Uri? Address => Href is { } href ? Resolve(href) : null;

    protected override Type StyleKeyOverride => typeof(Button);
}

/// <summary>A link inside the page was followed.</summary>
public sealed class NavigateEventArgs(RoutedEvent routedEvent, string href) : RoutedEventArgs(routedEvent)
{
    public string Href { get; } = href;
}
