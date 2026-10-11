namespace SSGen.Scene.Editing;

/// <summary>A point in canvas pixels.</summary>
public readonly record struct Pt(double X, double Y)
{
    public static Pt operator +(Pt a, Pt b) => new(a.X + b.X, a.Y + b.Y);

    public static Pt operator -(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>A corner of a layer's box: -1 = left/top, +1 = right/bottom.</summary>
public readonly record struct Corner(int Sx, int Sy)
{
    public static readonly Corner TopLeft = new(-1, -1);
    public static readonly Corner TopRight = new(1, -1);
    public static readonly Corner BottomLeft = new(-1, 1);
    public static readonly Corner BottomRight = new(1, 1);
    public static readonly IReadOnlyList<Corner> All = [TopLeft, TopRight, BottomRight, BottomLeft];
}

/// <summary>
/// Geometry for the editor: hit testing, corners of rotated layers, resizing from a corner and rotating towards
/// the pointer. Layers rotate around their centre, as the renderer draws them.
/// </summary>
public static class SceneGeometry
{
    public static Pt Center(Layer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return new(layer.X + (layer.W / 2), layer.Y + (layer.H / 2));
    }

    /// <summary>A canvas point in the layer's own (unrotated) frame, relative to its centre.</summary>
    public static Pt ToLocal(Layer layer, Pt p) => Rotate(p - Center(layer), -layer.Rotation);

    public static Pt ToCanvas(Layer layer, Pt local) => Center(layer) + Rotate(local, layer.Rotation);

    public static Pt CornerPoint(Layer layer, Corner corner)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return ToCanvas(layer, new Pt(corner.Sx * layer.W / 2, corner.Sy * layer.H / 2));
    }

    /// <summary>Whether a canvas point is inside the (rotated) layer box, with an optional tolerance in pixels.</summary>
    public static bool Contains(Layer layer, Pt p, double tolerance = 0)
    {
        var local = ToLocal(layer, p);
        return Math.Abs(local.X) <= (layer.W / 2) + tolerance && Math.Abs(local.Y) <= (layer.H / 2) + tolerance;
    }

    /// <summary>The topmost visible, unlocked layer under the point (or null).</summary>
    public static Layer? HitTest(SceneDocument scene, Pt p, double tolerance = 0)
    {
        ArgumentNullException.ThrowIfNull(scene);
        for (var i = scene.Layers.Count - 1; i >= 0; i--)
        {
            var layer = scene.Layers[i];
            if (layer.Visible && !layer.Locked && Contains(layer, p, tolerance))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>
    /// Resizes by dragging <paramref name="corner"/> to the point <paramref name="to"/> while the opposite corner stays
    /// where it is (also for rotated layers). <paramref name="keepAspect"/> keeps the current proportions.
    /// </summary>
    public static void ResizeFromCorner(Layer layer, Corner corner, Pt to, bool keepAspect, double minSize = 8)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var anchor = new Pt(-corner.Sx * layer.W / 2, -corner.Sy * layer.H / 2);
        var p = ToLocal(layer, to);
        var w = Math.Max(minSize, corner.Sx * (p.X - anchor.X));
        var h = Math.Max(minSize, corner.Sy * (p.Y - anchor.Y));
        if (keepAspect && layer.W > 0 && layer.H > 0)
        {
            var scale = Math.Max(w / layer.W, h / layer.H);
            w = Math.Max(minSize, layer.W * scale);
            h = Math.Max(minSize, layer.H * scale);
        }

        var anchorCanvas = ToCanvas(layer, anchor);
        var centre = anchorCanvas + Rotate(new Pt(corner.Sx * w / 2, corner.Sy * h / 2), layer.Rotation);
        layer.W = w;
        layer.H = h;
        layer.X = centre.X - (w / 2);
        layer.Y = centre.Y - (h / 2);
    }

    /// <summary>Rotation (degrees) that points the layer's top towards the pointer; snaps to 15° steps when asked.</summary>
    public static double RotationTowards(Layer layer, Pt to, bool snap)
    {
        var c = Center(layer);
        var degrees = (Math.Atan2(to.Y - c.Y, to.X - c.X) * 180 / Math.PI) + 90;
        if (snap)
        {
            degrees = Math.Round(degrees / 15) * 15;
        }

        return Normalize(degrees);
    }

    /// <summary>Degrees in (-180, 180].</summary>
    public static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees > 180)
        {
            degrees -= 360;
        }
        else if (degrees <= -180)
        {
            degrees += 360;
        }

        return Math.Abs(degrees) < 1e-9 ? 0 : degrees;
    }

    private static Pt Rotate(Pt p, double degrees)
    {
        if (degrees == 0)
        {
            return p;
        }

        var (sin, cos) = Math.SinCos(degrees * Math.PI / 180);
        return new Pt((p.X * cos) - (p.Y * sin), (p.X * sin) + (p.Y * cos));
    }
}
