using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SSGen.App.ViewModels;
using SSGen.Scene;
using SSGen.Scene.Editing;

namespace SSGen.App.Views;

/// <summary>
/// Editor page. The stage is a grid in canvas pixels (scaled by a LayoutTransform), so pointer positions read
/// relative to it are already in document coordinates. Selection handles are drawn in WPF over the SkiaSharp
/// render; their screen size stays constant at every zoom.
/// </summary>
public partial class EditorView : UserControl
{
    private const double HandleSize = 10;       // screen pixels
    private const double RotateDistance = 28;   // screen pixels above the top edge
    private const double SnapDistance = 8;      // screen pixels

    private enum DragMode
    {
        None,
        Move,
        Resize,
        Rotate,
    }

    private DragMode _mode;
    private Corner _corner;
    private Pt _start;
    private (double X, double Y) _origin;
    private (bool X, bool Y) _snapped;
    private bool _moved;
    private EditorViewModel? _vm;

    public EditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is EditorViewModel old)
            {
                old.SceneChanged -= OnSceneChanged;
                old.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _vm = e.NewValue as EditorViewModel;
            if (_vm is not null)
            {
                _vm.SceneChanged += OnSceneChanged;
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
        Loaded += (_, _) =>
        {
            if (_vm is { PaneWeights: [var a, var b, var c] })
            {
                LayersColumn.Width = new GridLength(a, GridUnitType.Star);
                CanvasColumn.Width = new GridLength(b, GridUnitType.Star);
                PropertiesColumn.Width = new GridLength(c, GridUnitType.Star);
            }

            UpdateZoom();
            DrawOverlay();
        };
        Unloaded += (_, _) => _vm?.CancelGesture();
    }

    private double Scale => StageScale.ScaleX <= 0 ? 1 : StageScale.ScaleX;

    // ------------------------------------------------------------------ zoom

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.DocWidth) or nameof(EditorViewModel.DocHeight))
        {
            Dispatcher.InvokeAsync(UpdateZoom);
        }
    }

    private void OnZoomChanged(object sender, RoutedEventArgs e) => UpdateZoom();

    private void OnCanvasAreaSizeChanged(object sender, SizeChangedEventArgs e) => UpdateZoom();

    private void UpdateZoom()
    {
        if (_vm is null || StageScale is null || CanvasScroll is null)
        {
            return;
        }

        double scale;
        if (Zoom100.IsChecked == true)
        {
            scale = 1;
        }
        else if (Zoom50.IsChecked == true)
        {
            scale = 0.5;
        }
        else
        {
            var w = Math.Max(50, CanvasScroll.ActualWidth - 56);
            var h = Math.Max(50, CanvasScroll.ActualHeight - 56);
            scale = Math.Min(w / Math.Max(1, _vm.DocWidth), h / Math.Max(1, _vm.DocHeight));
        }

        StageScale.ScaleX = StageScale.ScaleY = Math.Clamp(scale, 0.02, 4);
        DrawOverlay();
    }

    // ------------------------------------------------------------------ selection overlay

    private void OnSceneChanged(object? sender, EventArgs e) => DrawOverlay();

    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        if (_vm?.Selected is not { } layer)
        {
            return;
        }

        var s = 1 / Scale;
        var accent = (Brush)FindResource("NavSelectedBrush");
        var corners = Corner.All.Select(c => SceneGeometry.CornerPoint(layer, c)).ToList();
        var outline = new Polygon
        {
            Points = new PointCollection(corners.Select(p => new Point(p.X, p.Y))),
            Stroke = accent,
            StrokeThickness = 1.5 * s,
            StrokeDashArray = layer.Locked ? [4, 3] : null,
        };
        Overlay.Children.Add(outline);
        if (layer.Locked)
        {
            return;
        }

        // Rotation handle: a line up from the top edge to a round knob.
        var topMid = SceneGeometry.ToCanvas(layer, new Pt(0, -layer.H / 2));
        var knob = RotateHandle(layer);
        Overlay.Children.Add(new Line { X1 = topMid.X, Y1 = topMid.Y, X2 = knob.X, Y2 = knob.Y, Stroke = accent, StrokeThickness = 1.5 * s });
        AddHandle(knob, accent, s, round: true);

        foreach (var corner in corners)
        {
            AddHandle(corner, accent, s, round: false);
        }

        if (_mode == DragMode.Move && _vm.Document is { } doc)
        {
            var guide = (Brush)FindResource("SystemFillColorCriticalBrush");
            if (_snapped.X)
            {
                Overlay.Children.Add(new Line { X1 = doc.Width / 2.0, Y1 = 0, X2 = doc.Width / 2.0, Y2 = doc.Height, Stroke = guide, StrokeThickness = s, StrokeDashArray = [6, 4] });
            }

            if (_snapped.Y)
            {
                Overlay.Children.Add(new Line { X1 = 0, Y1 = doc.Height / 2.0, X2 = doc.Width, Y2 = doc.Height / 2.0, Stroke = guide, StrokeThickness = s, StrokeDashArray = [6, 4] });
            }
        }
    }

    private void AddHandle(Pt at, Brush accent, double s, bool round)
    {
        var size = HandleSize * s;
        Shape handle = round ? new Ellipse() : new Rectangle();
        handle.Width = size;
        handle.Height = size;
        handle.Fill = Brushes.White;
        handle.Stroke = accent;
        handle.StrokeThickness = 1.5 * s;
        Canvas.SetLeft(handle, at.X - (size / 2));
        Canvas.SetTop(handle, at.Y - (size / 2));
        Overlay.Children.Add(handle);
    }

    private Pt RotateHandle(Layer layer) => SceneGeometry.ToCanvas(layer, new Pt(0, (-layer.H / 2) - (RotateDistance / Scale)));

    // ------------------------------------------------------------------ pointer

    private static Pt ToPt(Point p) => new(p.X, p.Y);

    private static double Distance(Pt a, Pt b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.Document is null)
        {
            return;
        }

        Stage.Focus();
        var p = ToPt(e.GetPosition(Stage));
        var grab = HandleSize / Scale;

        // Handles of the current selection first.
        if (_vm.Selected is { Locked: false } selected)
        {
            if (Distance(p, RotateHandle(selected)) <= grab)
            {
                StartDrag(DragMode.Rotate, p, selected);
                e.Handled = true;
                return;
            }

            foreach (var corner in Corner.All)
            {
                if (Distance(p, SceneGeometry.CornerPoint(selected, corner)) <= grab)
                {
                    _corner = corner;
                    StartDrag(DragMode.Resize, p, selected);
                    e.Handled = true;
                    return;
                }
            }
        }

        var hit = _vm.LayerAt(p, 2 / Scale);
        _vm.Select(hit?.Id);
        if (hit is null)
        {
            return;
        }

        if (e.ClickCount == 2 && hit is TextLayer)
        {
            // Double-click a text: type in the properties box.
            Dispatcher.InvokeAsync(() =>
            {
                LayerText.Focus();
                LayerText.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
            return;
        }

        StartDrag(DragMode.Move, p, hit);
        e.Handled = true;
    }

    private void StartDrag(DragMode mode, Pt p, Layer layer)
    {
        _mode = mode;
        _start = p;
        _origin = (layer.X, layer.Y);
        _snapped = (false, false);
        _moved = false;
        _vm!.BeginGesture();
        Stage.CaptureMouse();
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.Document is not { } doc)
        {
            return;
        }

        var p = ToPt(e.GetPosition(Stage));
        if (_mode == DragMode.None)
        {
            UpdateCursor(p);
            return;
        }

        // A click that only selects must not change anything: start once the pointer really moves.
        if (!_moved && Distance(p, _start) < 3 / Scale)
        {
            return;
        }

        _moved = true;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_mode)
        {
            case DragMode.Move:
                _vm.UpdateGesture(l =>
                {
                    var x = _origin.X + (p.X - _start.X);
                    var y = _origin.Y + (p.Y - _start.Y);

                    // Snap the layer's centre to the canvas centre lines.
                    var threshold = SnapDistance / Scale;
                    _snapped = (Math.Abs(x + (l.W / 2) - (doc.Width / 2.0)) < threshold, Math.Abs(y + (l.H / 2) - (doc.Height / 2.0)) < threshold);
                    l.X = Math.Round(_snapped.X ? (doc.Width - l.W) / 2 : x);
                    l.Y = Math.Round(_snapped.Y ? (doc.Height - l.H) / 2 : y);
                });
                break;

            case DragMode.Resize:
                _vm.UpdateGesture(l =>
                {
                    // Pictures and devices keep their proportions unless Shift is held; other layers the reverse.
                    var keep = l is ImageLayer or DeviceLayer ? !shift : shift;
                    SceneGeometry.ResizeFromCorner(l, _corner, p, keep, minSize: 8 / Scale);
                });
                break;

            case DragMode.Rotate:
                _vm.UpdateGesture(l => l.Rotation = SceneGeometry.RotationTowards(l, p, snap: shift));
                break;
        }
    }

    private void UpdateCursor(Pt p)
    {
        var grab = HandleSize / Scale;
        if (_vm?.Selected is { Locked: false } selected)
        {
            if (Distance(p, RotateHandle(selected)) <= grab)
            {
                Stage.Cursor = Cursors.Hand;
                return;
            }

            foreach (var corner in Corner.All)
            {
                if (Distance(p, SceneGeometry.CornerPoint(selected, corner)) <= grab)
                {
                    Stage.Cursor = corner.Sx == corner.Sy ? Cursors.SizeNWSE : Cursors.SizeNESW;
                    return;
                }
            }
        }

        Stage.Cursor = _vm?.LayerAt(p, 2 / Scale) is not null ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == DragMode.None)
        {
            return;
        }

        var name = _mode switch
        {
            DragMode.Resize => "Resize",
            DragMode.Rotate => "Rotate",
            _ => "Move",
        };
        _mode = DragMode.None;
        _vm?.EndGesture(name);
        Stage.ReleaseMouseCapture();
        DrawOverlay();
    }

    private void OnStageLostCapture(object sender, MouseEventArgs e)
    {
        if (_mode != DragMode.None)
        {
            _mode = DragMode.None;
            _vm?.EndGesture("Move");
            DrawOverlay();
        }
    }

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && e.Key == Key.S)
        {
            // Commit the box being typed in first.
            (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            _vm.SaveChangesCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Typing in a box or using a list, slider or picker: leave its keys alone.
        if (e.OriginalSource is TextBoxBase or ComboBox or ComboBoxItem or Slider)
        {
            return;
        }

        // Arrow keys move the selection only when the canvas has focus (in the Layers list they move the list).
        var onCanvas = ReferenceEquals(e.OriginalSource, Stage);

        switch (e.Key)
        {
            case Key.Escape when _mode != DragMode.None:
                _mode = DragMode.None;
                _vm.CancelGesture();
                Stage.ReleaseMouseCapture();
                DrawOverlay();
                break;
            case Key.Escape:
                _vm.Select(null);
                break;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                _vm.RedoCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                _vm.UndoCommand.Execute(null);
                break;
            case Key.D when ctrl:
                _vm.DuplicateLayerCommand.Execute(null);
                break;
            case Key.Delete when _vm.HasSelection:
                _vm.DeleteLayerCommand.Execute(null);
                break;
            case Key.Left when onCanvas && _vm.HasSelection:
                _vm.Nudge(shift ? -10 : -1, 0);
                break;
            case Key.Right when onCanvas && _vm.HasSelection:
                _vm.Nudge(shift ? 10 : 1, 0);
                break;
            case Key.Up when onCanvas && _vm.HasSelection:
                _vm.Nudge(0, shift ? -10 : -1);
                break;
            case Key.Down when onCanvas && _vm.HasSelection:
                _vm.Nudge(0, shift ? 10 : 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Enter in a number box commits it.</summary>
    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
        }
    }

    // ------------------------------------------------------------------ panes

    private ColumnDefinition[] Columns => [LayersColumn, CanvasColumn, PropertiesColumn];

    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e) => Normalize();

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        Normalize();
        _vm?.SavePaneWeights(Columns.Select(c => c.ActualWidth));
    }

    /// <summary>Star weights = current pixel widths, so a drag moves only the splitter being dragged.</summary>
    private void Normalize()
    {
        foreach (var column in Columns)
        {
            column.Width = new GridLength(Math.Max(1, column.ActualWidth), GridUnitType.Star);
        }
    }
}
