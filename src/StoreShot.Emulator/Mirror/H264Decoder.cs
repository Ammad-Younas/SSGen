using System.Text.RegularExpressions;
using FFmpeg.AutoGen;
using StoreShot.Core;

namespace StoreShot.Emulator.Mirror;

/// <summary>
/// Decodes an Annex-B H.264 stream (as sent by scrcpy) to BGRA using FFmpeg's LGPL shared libraries,
/// loaded dynamically from tools\ffmpeg next to the app.
/// </summary>
public sealed unsafe partial class H264Decoder : IDisposable
{
    private static readonly Lock LoadGate = new();
    private static string? _loadedFrom;

    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;
    private byte[] _bgra = [];
    private int _swsWidth, _swsHeight;
    private AVPixelFormat _swsFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    public H264Decoder(string ffmpegDirectory)
    {
        EnsureLoaded(ffmpegDirectory);
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null)
        {
            throw new UserFacingException("The bundled FFmpeg has no H.264 decoder.", "Reinstall SSGen.");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _context->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _context->thread_count = 2;
        Check(ffmpeg.avcodec_open2(_context, codec, null), "open decoder");
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
    }

    [GeneratedRegex(@"^(avcodec|avutil|swscale|swresample)-(\d+)\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryFile();

    /// <summary>True if the FFmpeg DLLs are present in the folder (does not load them).</summary>
    public static bool IsAvailable(string directory) =>
        Directory.Exists(directory) &&
        Directory.EnumerateFiles(directory, "avcodec-*.dll").Any() &&
        Directory.EnumerateFiles(directory, "swscale-*.dll").Any();

    private static void EnsureLoaded(string directory)
    {
        lock (LoadGate)
        {
            if (_loadedFrom is not null)
            {
                return;
            }

            if (!IsAvailable(directory))
            {
                throw new UserFacingException("The video decoder (FFmpeg) isn't installed.", "Reinstall SSGen. The preview will fall back to slower screenshots.");
            }

            // Map library names to whatever major versions are actually present.
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
            {
                var m = LibraryFile().Match(Path.GetFileName(file));
                if (m.Success)
                {
                    ffmpeg.LibraryVersionMap[m.Groups[1].Value.ToLowerInvariant()] = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            ffmpeg.RootPath = directory;
            DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _loadedFrom = directory;
        }
    }

    /// <summary>Feeds one packet; calls <paramref name="onFrame"/> for each decoded frame (BGRA, tightly packed).</summary>
    public void Decode(ReadOnlySpan<byte> data, long pts, Action<ReadOnlySpan<byte>, int, int> onFrame)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        ObjectDisposedException.ThrowIf(_context is null, this);
        Check(ffmpeg.av_new_packet(_packet, data.Length), "allocate packet");
        data.CopyTo(new Span<byte>(_packet->data, data.Length));
        _packet->pts = pts;
        var sent = ffmpeg.avcodec_send_packet(_context, _packet);
        ffmpeg.av_packet_unref(_packet);
        if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
        {
            return; // Corrupt packet: skip it, the next key frame recovers.
        }

        while (true)
        {
            var r = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (r == ffmpeg.AVERROR(ffmpeg.EAGAIN) || r == ffmpeg.AVERROR_EOF || r < 0)
            {
                return;
            }

            ConvertAndEmit(onFrame);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    public void Dispose()
    {
        if (_sws is not null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        if (_frame is not null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_packet is not null)
        {
            var p = _packet;
            ffmpeg.av_packet_free(&p);
            _packet = null;
        }

        if (_context is not null)
        {
            var c = _context;
            ffmpeg.avcodec_free_context(&c);
            _context = null;
        }
    }

    private void ConvertAndEmit(Action<ReadOnlySpan<byte>, int, int> onFrame)
    {
        int w = _frame->width, h = _frame->height;
        var format = (AVPixelFormat)_frame->format;
        if (_sws is null || w != _swsWidth || h != _swsHeight || format != _swsFormat)
        {
            if (_sws is not null)
            {
                ffmpeg.sws_freeContext(_sws);
            }

            _sws = ffmpeg.sws_getContext(w, h, format, w, h, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            (_swsWidth, _swsHeight, _swsFormat) = (w, h, format);
            _bgra = new byte[w * h * 4];
        }

        fixed (byte* dst = _bgra)
        {
            var dstData = new byte_ptrArray4 { [0] = dst };
            var dstStride = new int_array4 { [0] = w * 4 };
            ffmpeg.sws_scale(_sws, _frame->data, _frame->linesize, 0, h, dstData, dstStride);
        }

        onFrame(_bgra, w, h);
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"FFmpeg failed to {what} (error {result}).");
        }
    }
}
