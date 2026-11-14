using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Harness.Ui.ViewModels;

namespace Harness.Ui.Views;

/// <summary>
/// A project's Work (<see cref="WorkViewModel"/>). Dragging with the pointer (app.js draggableTask, dropZone,
/// bpDraggable): a task row (drag-task) dropped on another blueprint's group, or on a chip of the Move to strip,
/// moves the task there; a blueprint's card or group header (drag-blueprint) dropped on another one
/// (drop-blueprint) is placed before or after it, by which half it lands on. A press that does not move is a click.
/// </summary>
public sealed partial class WorkView : UserControl
{
    /// <summary>How far the pointer moves before a press becomes a drag.</summary>
    public const double Threshold = 6;

    private Point _start;
    private object? _dragged;
    private bool _dragging;

    public WorkView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private WorkPlan? Plan => (DataContext as WorkViewModel)?.Plan;

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is not Visual source)
        {
            return;
        }
        // a click on a control inside the handle (an arrow, Edit, Delete) is that control's, never a drag
        var handle = source.GetSelfAndVisualAncestors().OfType<Control>().FirstOrDefault(c => c.Classes.Contains("drag-task") || c.Classes.Contains("drag-blueprint"));
        var inner = source.GetSelfAndVisualAncestors().OfType<Button>().FirstOrDefault();
        if (handle is null || (inner is not null && inner.Classes.Contains("icon")))
        {
            _dragged = null;
            return;
        }
        _dragged = handle.DataContext switch
        {
            TaskRow row => row,
            BlueprintCard card when card.HasBlueprint => card,
            TaskGroup group when group.Movable => group,
            _ => null,
        };
        _start = e.GetPosition(this);
        _dragging = false;
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_dragged is null || _dragging)
        {
            return;
        }
        var at = e.GetPosition(this);
        if (Math.Abs(at.X - _start.X) + Math.Abs(at.Y - _start.Y) < Threshold)
        {
            return;
        }
        _dragging = true;
        if (_dragged is TaskRow)
        {
            DropStrip.IsVisible = true;
        }
        Classes.Add("dragging");
        if (this.FindDescendantOfType<StackPanel>() is { } root)
        {
            root.Classes.Add("dragging");
        }
    }

    private async void Released(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _dragged;
        var dragging = _dragging;
        _dragged = null;
        _dragging = false;
        // what it lands on, before the Move to strip goes away
        var point = e.GetPosition(this);
        var target = dragging
            ? this.GetVisualsAt(point)
                .SelectMany(v => v.GetSelfAndVisualAncestors())
                .OfType<Control>()
                .FirstOrDefault(c => c.Classes.Contains("drop-blueprint"))
            : null;
        DropStrip.IsVisible = false;
        Classes.Remove("dragging");
        if (this.FindDescendantOfType<StackPanel>() is { } root)
        {
            root.Classes.Remove("dragging");
        }
        if (!dragging || dragged is null || Plan is not { } plan)
        {
            return;
        }
        // the drop is the drag's: no click on what it was released over
        e.Handled = true;
        var id = target?.DataContext switch
        {
            TaskGroup group => group.Id,
            BlueprintCard card => card.Id,
            _ => null,
        };
        if (id is null || target is null)
        {
            return;
        }
        switch (dragged)
        {
            case TaskRow row:
                await plan.MoveTaskAsync(row.Id, id);
                break;
            case BlueprintCard or TaskGroup when id is not "—":
                var from = dragged switch
                {
                    BlueprintCard card => card.Id,
                    TaskGroup group => group.Id,
                    _ => id,
                };
                var box = target.Bounds;
                var local = this.TranslatePoint(point, target) ?? default;
                // cards side by side take it on their left or right half, stacked groups above or below
                var after = target.DataContext is BlueprintCard ? local.X > box.Width / 2 : local.Y > box.Height / 2;
                await plan.PlaceAsync(from, id, after);
                break;
        }
    }
}
