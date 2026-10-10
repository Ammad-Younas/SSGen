namespace SSGen.Emulator.Mirror;

/// <summary>
/// Latest decoded frame (BGRA, tightly packed), shared between a decoder thread and the UI.
/// The producer overwrites; the consumer copies out when <see cref="Version"/> changes, so a slow UI simply
/// skips frames instead of queueing them.
/// </summary>
public sealed class FrameBuffer
{
    private readonly Lock _gate = new();
    private byte[] _pixels = [];
    private long _version;
    private long _frameCount;
    private DateTime _fpsWindowStart = DateTime.UtcNow;
    private double _fps;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public long Version => Interlocked.Read(ref _version);

    /// <summary>Frames published per second, measured over the last ~1 s window.</summary>
    public double FramesPerSecond => Volatile.Read(ref _fps);

    public void Publish(ReadOnlySpan<byte> bgra, int width, int height)
    {
        lock (_gate)
        {
            if (_pixels.Length != bgra.Length)
            {
                _pixels = new byte[bgra.Length];
            }

            bgra.CopyTo(_pixels);
            Width = width;
            Height = height;
            _version++;
            _frameCount++;
            var elapsed = DateTime.UtcNow - _fpsWindowStart;
            if (elapsed.TotalSeconds >= 1)
            {
                Volatile.Write(ref _fps, _frameCount / elapsed.TotalSeconds);
                _frameCount = 0;
                _fpsWindowStart = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Copies the frame if it is newer than <paramref name="lastVersion"/>. The callback runs under the lock.</summary>
    public bool TryRead(ref long lastVersion, ReadAction read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            if (_version == lastVersion || Width == 0)
            {
                return false;
            }

            read(_pixels, Width, Height);
            lastVersion = _version;
            return true;
        }
    }

    /// <summary>Encodes the latest frame as PNG (for "capture this screen"), or null if none yet.</summary>
    public byte[]? SnapshotPng()
    {
        lock (_gate)
        {
            if (Width == 0)
            {
                return null;
            }

            using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(Width, Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(_pixels, 0, bitmap.GetPixels(), _pixels.Length);
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
    }

    public delegate void ReadAction(byte[] pixels, int width, int height);
}
