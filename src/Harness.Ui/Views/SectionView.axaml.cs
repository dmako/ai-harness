using Avalonia;
using Avalonia.Controls;

namespace Harness.Ui.Views;

/// <summary>
/// The frame of a section: title, hint, loading, error with Retry, then its content. A view whose view model is the
/// section (HealthView) puts its controls in <see cref="Body"/>; without one the content is the DataTemplate of the
/// view model's type.
/// </summary>
public sealed partial class SectionView : UserControl
{
    public static readonly StyledProperty<object?> BodyProperty = AvaloniaProperty.Register<SectionView, object?>(
        nameof(Body)
    );

    public SectionView() => InitializeComponent();

    /// <summary>The section's content (set as SectionView.Body), shown once it has loaded.</summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    // the content is set here, never bound: a view that is its section's own DataTemplate would otherwise show itself
    // inside itself until Body replaced the binding
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BodyProperty || change.Property == DataContextProperty)
        {
            BodyHost.Content = Body ?? DataContext;
        }
    }
}
