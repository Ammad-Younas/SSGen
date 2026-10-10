using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StoreShot.App.ViewModels;
using StoreShot.Emulator.Devices;
using StoreShot.Emulator.Mirror;
using TouchAction = StoreShot.Emulator.Mirror.TouchAction;

namespace StoreShot.App.Views;

/// <summary>
/// Hosts the live preview. Frames are pulled from the mirror's FrameBuffer once per WPF render tick, so a slow UI
/// skips frames instead of queueing them; mouse/keyboard input is mapped to frame pixels and forwarded.
/// </summary>
public partial class EmulatorView : UserControl
{
    private WriteableBitmap? _bitmap;
    private long _frameVersion;
    private DateTime _lastFpsUpdate;
    private DateTime _lastFrameAt;
    private bool _dragging;

    public EmulatorView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            CompositionTarget.Rendering += OnRendering;
            if (Vm?.PaneWeights is [var l, var c, var r])
            {
                LeftColumn.Width = new GridLength(l, GridUnitType.Star);
                CenterColumn.Width = new GridLength(c, GridUnitType.Star);
                RightColumn.Width = new GridLength(r, GridUnitType.Star);
            }
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private EmulatorViewModel? Vm => DataContext as EmulatorViewModel;

    /// <summary>
    /// GridSplitter rewrites only the two adjacent star columns, in pixel-scale weights, leaving the third at its
    /// old weight, which then collapses to its MinWidth. Normalising all three to their current pixel widths first
    /// keeps every weight on the same scale.
    /// </summary>
    private void OnSplitterDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => NormalizePaneWeights();

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        NormalizePaneWeights();
        Vm?.SavePaneWeights(LeftColumn.ActualWidth, CenterColumn.ActualWidth, RightColumn.ActualWidth);
    }

    private void NormalizePaneWeights()
    {
        foreach (var column in new[] { LeftColumn, CenterColumn, RightColumn })
        {
            column.Width = new GridLength(Math.Max(1, column.ActualWidth), GridUnitType.Star);
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var mirror = Vm?.Mirror;
        if (mirror is null)
        {
            if (Preview.Source is not null)
            {
                Preview.Source = null;
                _bitmap = null;
                _frameVersion = 0;
                FpsRun.Text = "";
            }

            return;
        }

        var hadFrame = mirror.Frames.TryRead(ref _frameVersion, (pixels, w, h) =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                Preview.Source = _bitmap;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        });
        if (hadFrame)
        {
            _lastFrameAt = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - _lastFpsUpdate > TimeSpan.FromSeconds(1))
        {
            _lastFpsUpdate = DateTime.UtcNow;
            FpsRun.Text = mirror.Frames.Width == 0
                ? "· waiting for first frame…"
                : string.Create(CultureInfo.InvariantCulture, $"· {mirror.Frames.Width}×{mirror.Frames.Height} · {(mirror.Frames.FramesPerSecond >= 1 && _frameVersion > 0 && DateTime.UtcNow - _lastFrameAt < TimeSpan.FromSeconds(2) ? $"{mirror.Frames.FramesPerSecond:0} fps" : "idle (frames arrive when the screen changes)")}");
        }
    }

    private (int X, int Y)? ToFrame(MouseEventArgs e)
    {
        if (_bitmap is null || Preview.ActualWidth <= 0 || Preview.ActualHeight <= 0)
        {
            return null;
        }

        var p = e.GetPosition(Preview);
        var x = (int)Math.Clamp(p.X / Preview.ActualWidth * _bitmap.PixelWidth, 0, _bitmap.PixelWidth - 1);
        var y = (int)Math.Clamp(p.Y / Preview.ActualHeight * _bitmap.PixelHeight, 0, _bitmap.PixelHeight - 1);
        return (x, y);
    }

    private async void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        Preview.Focus();
        if (Vm?.Mirror is { } mirror && ToFrame(e) is { } pt)
        {
            _dragging = true;
            Preview.CaptureMouse();
            await mirror.TouchAsync(TouchAction.Down, pt.X, pt.Y);
        }
    }

    private async void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging && Vm?.Mirror is { } mirror && ToFrame(e) is { } pt)
        {
            await mirror.TouchAsync(TouchAction.Move, pt.X, pt.Y);
        }
    }

    private async void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Preview.ReleaseMouseCapture();
        if (Vm?.Mirror is { } mirror && ToFrame(e) is { } pt)
        {
            await mirror.TouchAsync(TouchAction.Up, pt.X, pt.Y);
        }
    }

    private async void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm?.Mirror is { } mirror && ToFrame(e) is { } pt)
        {
            await mirror.ScrollAsync(pt.X, pt.Y, e.Delta / 120f);
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        AndroidKey? key = e.Key switch
        {
            Key.Back => AndroidKey.Delete,
            Key.Enter => AndroidKey.Enter,
            Key.Tab => AndroidKey.Tab,
            Key.Escape => AndroidKey.Back,
            Key.Up => AndroidKey.DpadUp,
            Key.Down => AndroidKey.DpadDown,
            _ => null,
        };
        if (key is { } k && Vm?.Mirror is { } mirror)
        {
            e.Handled = true;
            await mirror.KeyAsync(k);
        }
    }

    private async void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Vm?.Mirror is { } mirror && !string.IsNullOrEmpty(e.Text) && !char.IsControl(e.Text[0]))
        {
            e.Handled = true;
            await mirror.TextAsync(e.Text);
        }
    }

    private void OnTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.SendTextCommand.CanExecute(null) == true)
        {
            Vm.SendTextCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Keeps the log scrolled to the newest line unless the user scrolled up.</summary>
    private void OnLogListLoaded(object sender, RoutedEventArgs e)
    {
        // Follow the tail: stay pinned to the newest line until the user scrolls up; resume when they scroll back
        // to the bottom. ScrollChanged fires after layout, so this also works when the panel was hidden.
        var list = (ListView)sender;
        if (FindChild<ScrollViewer>(list) is not { } viewer || _logViewer == viewer)
        {
            return;
        }

        _logViewer = viewer;
        viewer.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            {
                _followTail = viewer.VerticalOffset >= viewer.ScrollableHeight - 1; // user scrolled
            }
            else if (_followTail)
            {
                viewer.ScrollToEnd(); // content grew or panel resized/shown
            }
        };
        _followTail = true;
        viewer.ScrollToEnd();
    }

    /// <summary>The log list has no template while its panel is collapsed; set up following once it is shown.</summary>
    private void OnLogListVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => OnLogListLoaded(sender, new RoutedEventArgs()));
        }
    }

    private ScrollViewer? _logViewer;
    private bool _followTail = true;

    private double _wheelRemainder;

    /// <summary>
    /// Scrolls the log by wheel/touchpad. Precision touchpads send many small deltas (well under 120 per event);
    /// accumulating them gives smooth two-finger scrolling instead of ignoring or over-jumping.
    /// </summary>
    private void OnLogMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindChild<ScrollViewer>((DependencyObject)sender) is not { } viewer)
        {
            return;
        }

        // Item-based scrolling: offsets are in lines. 3 lines per wheel notch (120).
        _wheelRemainder += -e.Delta / 120.0 * 3;
        var lines = Math.Truncate(_wheelRemainder);
        if (lines != 0)
        {
            _wheelRemainder -= lines;
            viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset + lines, 0, viewer.ScrollableHeight));
        }

        e.Handled = true;
    }

    private static T? FindChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
