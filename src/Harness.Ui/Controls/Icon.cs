using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Harness.Ui.Controls;

/// <summary>
/// A design-system icon (Styles/Icons.axaml), drawn in the text colour like app.js's icon(): Icon.&lt;kind&gt;
/// stroked 1.8 wide on a 24-unit grid with round caps and joins, IconFill.&lt;kind&gt; filled. Decorative: it is
/// hidden from screen readers, so the control it sits in carries the name.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(
        nameof(Size),
        16
    );

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
        AutomationProperties.AccessibilityViewProperty.OverrideDefaultValue<Icon>(AccessibilityView.Raw);
        FocusableProperty.OverrideDefaultValue<Icon>(false);
    }

    /// <summary>The icon's name in Styles/Icons.axaml, e.g. edit, trash, p-high, status-ready.</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Width and height in pixels (16 as app.js draws them).</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Kind is not { Length: > 0 } kind || Foreground is not { } brush)
        {
            return;
        }
        var scale = Size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            if (Find("IconFill." + kind) is { } fill)
            {
                context.DrawGeometry(brush, null, fill);
            }
            if (Find("Icon." + kind) is { } stroke)
            {
                context.DrawGeometry(
                    null,
                    new Pen(brush, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                    stroke
                );
            }
        }
    }

    private Geometry? Find(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as Geometry : null;
}
