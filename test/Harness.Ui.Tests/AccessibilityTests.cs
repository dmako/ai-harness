using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Harness.Ui.Controls;
using Harness.Ui.Views;
using Xunit;

namespace Harness.Ui.Tests;

public sealed class AccessibilityTests
{
    [Fact]
    public Task PickerOpensAndClosesWithKeyboard() => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800);
        var picker = window.GetVisualDescendants().OfType<BoardView>().Single().FindControl<ComboBox>("SortPicker")!;
        picker.Focus();
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Ui.Settle();
        picker.IsDropDownOpen.Should().BeTrue();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Ui.Settle();
        picker.IsDropDownOpen.Should().BeFalse();
    });

    [Fact]
    public Task KeyboardFocusShowsTheAccentRingOnTaskButtons() => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800);
        Button? focused = null;
        for (var i = 0; i < 120 && focused is null; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            focused = window.FocusManager.GetFocusedElement() as Button;
            if (focused?.Name is not "OpenTask")
            {
                focused = null;
            }
        }
        focused.Should().NotBeNull();
        // brand.css :focus-visible: an Accent outline 2 px outside the button, its corners following the button's
        var ring = window.GetVisualDescendants().OfType<FocusRing>()
            .Single(r => ReferenceEquals(AdornerLayer.GetAdornedElement(r), focused));
        ring.TryFindResource("Accent", ring.ActualThemeVariant, out var accent).Should().BeTrue();
        ring.BorderBrush.Should().Be(accent);
        ring.BorderThickness.Left.Should().Be(2);
        ring.Margin.Left.Should().Be(-4);
        ring.Bounds.Width.Should().BeApproximately(focused.Bounds.Width + 8, 0.5);
    });

    [Theory]
    [InlineData("#/")]
    [InlineData("#/p/prj_fixture/overview")]
    [InlineData("#/p/prj_fixture/work/tasks")]
    [InlineData("#/p/prj_fixture/work/blueprints")]
    [InlineData("#/p/prj_fixture/work/inbox")]
    [InlineData("#/p/prj_fixture/board")]
    [InlineData("#/p/prj_fixture/knowledge")]
    [InlineData("#/p/prj_fixture/knowledge/inbox")]
    [InlineData("#/p/prj_fixture/agents")]
    [InlineData("#/p/prj_fixture/agents/tools")]
    [InlineData("#/p/prj_fixture/architecture")]
    [InlineData("#/p/prj_fixture/architecture/layers")]
    [InlineData("#/p/prj_fixture/architecture/complexity")]
    [InlineData("#/p/prj_fixture/architecture/coupling")]
    [InlineData("#/p/prj_fixture/health")]
    [InlineData("#/w/main/overview")]
    [InlineData("#/w/main/architecture")]
    [InlineData("#/w/main/architecture/build")]
    [InlineData("#/w/main/architecture/libraries")]
    [InlineData("#/w/main/knowledge")]
    [InlineData("#/w/main/issues")]
    [InlineData("#/w/main/issues/versions")]
    [InlineData("#/w/main/members")]
    [InlineData("#/machine")]
    [InlineData("#/knowledge")]
    [InlineData("#/search")]
    [InlineData("#/docs/guide")]
    public Task ViewControlsHaveNamesAndAreReachableByTab(string route) => Ui.RunAsync(async () =>
    {
        var (window, _, _) = await Ui.OpenMainAsync(1280, 800, address: route);
        await Task.Delay(200);
        Ui.Settle();
        var page = window.GetVisualDescendants().OfType<MainView>().Single().FindControl<ContentControl>("Page")!;
        var controls = page.GetVisualDescendants().OfType<Control>()
            .Where(c => c is Button or ToggleButton or TextBox or ComboBox or CheckBox or Expander or AppLink or DependencyMap or ScrollViewer)
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Focusable && c.IsTabStop)
            .Where(c => c.Name is not "ExpanderHeader")
            .Where(c => !c.GetVisualAncestors().TakeWhile(a => a != page)
                .Any(a => a is Button or ToggleButton or ComboBox or CheckBox or AppLink))
            .ToArray();
        foreach (var control in controls)
        {
            AutomationProperties.GetName(control).Should().NotBeNullOrWhiteSpace($"{route}: {control.GetType().Name} {control.Name}");
        }
        var visited = new HashSet<IInputElement>();
        for (var i = 0; i < 300; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
            Dispatcher.UIThread.RunJobs();
            if (window.FocusManager.GetFocusedElement() is not { } focused || !visited.Add(focused))
            {
                break;
            }
        }
        foreach (var control in controls)
        {
            visited.Should().Contain(control, $"{route}: {control.GetType().Name} {control.Name} must be reachable with Tab");
        }
    });
}
