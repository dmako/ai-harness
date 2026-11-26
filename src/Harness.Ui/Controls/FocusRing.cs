using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Harness.Ui.Controls;

/// <summary>
/// The keyboard focus ring every control shows in place of Fluent's square white-and-black adorner (brand.css
/// <c>:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px }</c>): a 2 px Accent line 2 px outside
/// the control, its corners following the control's own.
/// </summary>
public sealed class FocusRing : Border
{
    /// <summary>The ring's line width.</summary>
    public const double Thickness = 2;

    /// <summary>The gap between the control and the ring.</summary>
    public const double Offset = 2;

    public FocusRing()
    {
        IsHitTestVisible = false;
        BorderThickness = new Thickness(Thickness);
        Margin = new Thickness(-(Thickness + Offset));
        // drawn outside the control: the adorner layer must not clip it to the control's bounds
        AdornerLayer.SetIsClipEnabled(this, false);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // the outline's corners grow by its distance from the control, as a browser's outline does
        var radius = AdornerLayer.GetAdornedElement(this) is TemplatedControl { CornerRadius: var r } ? r : default;
        var grow = Thickness + Offset;
        CornerRadius = new CornerRadius(
            Grow(radius.TopLeft, grow),
            Grow(radius.TopRight, grow),
            Grow(radius.BottomRight, grow),
            Grow(radius.BottomLeft, grow)
        );
    }

    private static double Grow(double radius, double by) => radius > 0 ? radius + by : 0;
}
