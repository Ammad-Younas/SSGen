using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SSGen.App.Services;

/// <summary>
/// Gets the user's attention when something needs them (exploration finished, a pause for sign-in or payment):
/// a sound and, when SSGen isn't the active window, a flashing taskbar button until it is.
/// </summary>
public static partial class Attention
{
    private const uint FlashAll = 0x3;           // caption and taskbar button
    private const uint FlashUntilForeground = 0xC;

    public static void Request()
    {
        SystemSounds.Asterisk.Play();
        if (Application.Current?.MainWindow is not { } window || window.IsActive)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(window).Handle,
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}
