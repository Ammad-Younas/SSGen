using System.Collections.Specialized;
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
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    private EmulatorViewModel? Vm => DataContext as EmulatorViewModel;

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
        var list = (ListView)sender;
        if (list.ItemsSource is INotifyCollectionChanged changes)
        {
            changes.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add && list.Items.Count > 0 && IsNearBottom(list))
                {
                    list.ScrollIntoView(list.Items[^1]);
                }
            };
        }
    }

    private static bool IsNearBottom(DependencyObject list)
    {
        var viewer = FindChild<ScrollViewer>(list);
        return viewer is null || viewer.VerticalOffset >= viewer.ScrollableHeight - 3;
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
