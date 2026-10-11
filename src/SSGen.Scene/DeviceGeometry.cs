using SkiaSharp;

namespace SSGen.Scene;

/// <summary>
/// Proportions of the procedurally drawn device frames (original artwork; no third-party device images).
/// All measures are fractions of the device body's width, so a frame scales to any size.
/// </summary>
public static class DeviceGeometry
{
    /// <summary>Bezel thickness, body corner radius and camera size, as fractions of the body width.</summary>
    public static (double Bezel, double Corner, double Camera) Proportions(DeviceFrame frame) => frame switch
    {
        DeviceFrame.PhonePunchHole => (0.030, 0.125, 0.034),
        DeviceFrame.PhoneIsland => (0.028, 0.150, 0.030),
        DeviceFrame.Tablet => (0.034, 0.055, 0.012),
        _ => (0, 0.06, 0),
    };

    /// <summary>Body width/height ratio for a screen of the given width/height ratio.</summary>
    public static double BodyAspect(DeviceFrame frame, double screenAspect)
    {
        var bezel = Proportions(frame).Bezel;

        // bodyW = sw / (1 - 2b); bodyH = sh + 2b·bodyW. With sw = aspect, sh = 1:
        var bodyW = screenAspect / (1 - (2 * bezel));
        var bodyH = 1 + (2 * bezel * bodyW);
        return bodyW / bodyH;
    }

    /// <summary>The screen area inside a device body rectangle, and its corner radius.</summary>
    public static (SKRect Screen, float Radius) Screen(DeviceFrame frame, SKRect body)
    {
        var (bezel, corner, _) = Proportions(frame);
        var b = (float)(bezel * body.Width);
        var screen = new SKRect(body.Left + b, body.Top + b, body.Right - b, body.Bottom - b);
        var radius = (float)Math.Max(0, (corner * body.Width) - b);
        return (screen, radius);
    }

    public static float BodyRadius(DeviceFrame frame, SKRect body) => (float)(Proportions(frame).Corner * body.Width);
}
