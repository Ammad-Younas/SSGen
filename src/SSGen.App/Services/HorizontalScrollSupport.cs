using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SSGen.App.Services;

/// <summary>
/// WPF has no horizontal mouse-wheel event. Two-finger horizontal touchpad gestures (and tilt wheels) arrive as
/// WM_MOUSEHWHEEL, so this hooks the window and scrolls the nearest horizontally scrollable ScrollViewer under the
/// pointer. Shift + vertical wheel scrolls horizontally too (for mice without tilt).
/// </summary>
public static class HorizontalScrollSupport
{
    private const int WmMouseHWheel = 0x020E;
    private const double PixelsPerNotch = 60;

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) =>
            HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(Hook);
        window.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift && ScrollHorizontally(-e.Delta))
            {
                e.Handled = true;
            }
        };
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseHWheel)
        {
            // High word of wParam: signed delta, positive = scroll right.
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            handled = ScrollHorizontally(delta);
        }

        return IntPtr.Zero;
    }

    /// <summary>Scrolls the ScrollViewer under the mouse; returns false if there is nothing to scroll.</summary>
    internal static bool ScrollHorizontally(int delta)
    {
        for (var node = Mouse.DirectlyOver as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is ScrollViewer { ScrollableWidth: > 0 } viewer)
            {
                viewer.ScrollToHorizontalOffset(Math.Clamp(viewer.HorizontalOffset + (delta / 120.0 * PixelsPerNotch), 0, viewer.ScrollableWidth));
                return true;
            }
        }

        return false;
    }

    // Runs inside TextBlocks are ContentElements, not Visuals: fall back to the logical parent for those.
    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
