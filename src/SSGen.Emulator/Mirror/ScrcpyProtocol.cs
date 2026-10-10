using System.Buffers.Binary;
using System.Text;

namespace SSGen.Emulator.Mirror;

public enum TouchAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2,
}

/// <summary>
/// Wire format of the scrcpy server, pinned to <see cref="ServerVersion"/> (verified against the v5.0.1 sources:
/// DesktopConnection, Streamer, ControlMessageReader). The protocol is internal to scrcpy and changes between
/// versions, so the bundled server binary must always match this version exactly.
/// </summary>
public static class ScrcpyProtocol
{
    public const string ServerVersion = "5.0.1";
    public const int DeviceNameLength = 64;
    public const int HeaderLength = 12;
    public const uint CodecH264 = 0x68323634;
    public const int MaxTextBytes = 300;

    /// <summary>Pointer id the server treats as a finger (not a mouse), so apps see normal touches.</summary>
    public const long PointerIdGenericFinger = -2;

    private const byte TypeInjectKeycode = 0;
    private const byte TypeInjectText = 1;
    private const byte TypeInjectTouch = 2;
    private const byte TypeInjectScroll = 3;
    private const byte TypeBackOrScreenOn = 4;

    /// <summary>A parsed 12-byte header from the video socket.</summary>
    public readonly record struct PacketHeader(bool IsSession, int Width, int Height, bool IsConfig, bool IsKeyFrame, long Pts, int PacketSize);

    public static PacketHeader ParseHeader(ReadOnlySpan<byte> h)
    {
        if (h.Length < HeaderLength)
        {
            throw new ArgumentException("Header must be 12 bytes.", nameof(h));
        }

        if ((h[0] & 0x80) != 0)
        {
            // Session packet: flags u32, width u32, height u32. No payload follows.
            return new PacketHeader(true, BinaryPrimitives.ReadInt32BigEndian(h[4..]), BinaryPrimitives.ReadInt32BigEndian(h[8..]), false, false, 0, 0);
        }

        var ptsAndFlags = BinaryPrimitives.ReadUInt64BigEndian(h);
        var config = (ptsAndFlags & (1UL << 62)) != 0;
        var key = (ptsAndFlags & (1UL << 61)) != 0;
        var pts = (long)(ptsAndFlags & ((1UL << 61) - 1));
        var size = BinaryPrimitives.ReadInt32BigEndian(h[8..]);
        return new PacketHeader(false, 0, 0, config, key, config ? 0 : pts, size);
    }

    /// <summary>INJECT_TOUCH_EVENT (32 bytes). Coordinates are in video-frame pixels; width/height must equal the current frame size.</summary>
    public static byte[] Touch(TouchAction action, int x, int y, int frameWidth, int frameHeight, float pressure = 1f, long pointerId = PointerIdGenericFinger)
    {
        var b = new byte[32];
        b[0] = TypeInjectTouch;
        b[1] = (byte)action;
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(2), pointerId);
        WritePosition(b.AsSpan(10), x, y, frameWidth, frameHeight);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(22), ToU16FixedPoint(pressure));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(24), 0); // action button
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(28), 0); // buttons
        return b;
    }

    /// <summary>INJECT_KEYCODE (14 bytes). Action: 0 = down, 1 = up.</summary>
    public static byte[] Key(int keyAction, int keycode, int repeat = 0, int metaState = 0)
    {
        var b = new byte[14];
        b[0] = TypeInjectKeycode;
        b[1] = (byte)keyAction;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(2), keycode);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(6), repeat);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(10), metaState);
        return b;
    }

    /// <summary>INJECT_TEXT: u32 length + UTF-8, truncated to the server's 300-byte limit on a character boundary.</summary>
    public static byte[] Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextBytes)
        {
            var len = MaxTextBytes;
            while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            {
                len--; // don't split a UTF-8 sequence
            }

            bytes = bytes[..len];
        }

        var b = new byte[5 + bytes.Length];
        b[0] = TypeInjectText;
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), bytes.Length);
        bytes.CopyTo(b, 5);
        return b;
    }

    /// <summary>INJECT_SCROLL_EVENT (21 bytes). Scroll amounts are in "notches", clamped to [-16, 16].</summary>
    public static byte[] Scroll(int x, int y, int frameWidth, int frameHeight, float horizontal, float vertical)
    {
        var b = new byte[21];
        b[0] = TypeInjectScroll;
        WritePosition(b.AsSpan(1), x, y, frameWidth, frameHeight);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(13), ToI16FixedPoint(horizontal / 16f));
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(15), ToI16FixedPoint(vertical / 16f));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(17), 0);
        return b;
    }

    /// <summary>BACK_OR_SCREEN_ON (2 bytes): Back if the screen is on, otherwise wakes it.</summary>
    public static byte[] BackOrScreenOn(int keyAction) => [TypeBackOrScreenOn, (byte)keyAction];

    /// <summary>Server arguments (after the version) for a video+control session without audio.</summary>
    public static IReadOnlyList<string> ServerOptions(int scid, int maxSize, int maxFps, int bitRate, string logLevel = "info") =>
    [
        $"scid={scid:x8}",
        "log_level=" + logLevel,
        "tunnel_forward=true",
        "audio=false",
        "control=true",
        "cleanup=true",
        "video_codec=h264",
        FormattableString.Invariant($"max_size={maxSize}"),
        FormattableString.Invariant($"max_fps={maxFps}"),
        FormattableString.Invariant($"video_bit_rate={bitRate}"),
        "clipboard_autosync=false",
        "power_on=true",
    ];

    private static void WritePosition(Span<byte> b, int x, int y, int w, int h)
    {
        BinaryPrimitives.WriteInt32BigEndian(b, x);
        BinaryPrimitives.WriteInt32BigEndian(b[4..], y);
        BinaryPrimitives.WriteUInt16BigEndian(b[8..], (ushort)w);
        BinaryPrimitives.WriteUInt16BigEndian(b[10..], (ushort)h);
    }

    internal static ushort ToU16FixedPoint(float f)
    {
        f = Math.Clamp(f, 0f, 1f);
        return f >= 1f ? (ushort)0xFFFF : (ushort)(f * 65536f);
    }

    internal static short ToI16FixedPoint(float f)
    {
        f = Math.Clamp(f, -1f, 1f);
        return f >= 1f ? (short)0x7FFF : (short)(f * 32768f);
    }
}
