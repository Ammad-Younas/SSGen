using System.Text.Json;
using System.Text.Json.Serialization;

namespace SSGen.Scene;

/// <summary>
/// The one editable image format shared by templates (which produce it), the renderer (which draws it) and the
/// editor (M6, which changes it). Coordinates are canvas pixels; rotation is in degrees around a layer's centre.
/// Layers draw in list order (first = bottom). Nothing is flattened until export. New optional fields keep
/// <see cref="CurrentVersion"/>: older files simply don't have them.
/// </summary>
public sealed class SceneDocument
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public int Width { get; set; }

    public int Height { get; set; }

    public Fill Background { get; set; } = new SolidFill("#FFFFFF");

    public List<Layer> Layers { get; set; } = [];

    /// <summary>Where this scene came from (template id, preset id, screen id); informational.</summary>
    public Dictionary<string, string> Meta { get; set; } = [];

    public T? Find<T>(string id)
        where T : Layer => Layers.OfType<T>().FirstOrDefault(l => l.Id == id);

    public string ToJson() => JsonSerializer.Serialize(this, SceneJson.Options);

    public static SceneDocument FromJson(string json) =>
        JsonSerializer.Deserialize<SceneDocument>(json, SceneJson.Options) ?? throw new JsonException("Empty scene document.");

    public SceneDocument Clone() => FromJson(ToJson());
}

/// <summary>Shared JSON settings: camelCase, enums as strings, layer/fill types via a "type" field.</summary>
public static class SceneJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

// ------------------------------------------------------------------ fills

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SolidFill), "solid")]
[JsonDerivedType(typeof(LinearGradientFill), "linear")]
[JsonDerivedType(typeof(RadialGradientFill), "radial")]
public abstract record Fill;

/// <param name="Color">#RRGGBB or #AARRGGBB.</param>
public sealed record SolidFill(string Color) : Fill;

/// <param name="Angle">Degrees; 0 = left to right, 90 = top to bottom.</param>
/// <param name="Span">Scale of the gradient relative to the layer (panorama sets spread one gradient across several images).</param>
/// <param name="Offset">Horizontal shift in multiples of the layer width (panorama: index of the image in the set).</param>
public sealed record LinearGradientFill(IReadOnlyList<string> Colors, double Angle = 90, double Span = 1, double Offset = 0) : Fill;

/// <param name="Cx">Centre as a fraction of the layer width.</param>
/// <param name="Cy">Centre as a fraction of the layer height.</param>
/// <param name="Radius">Radius as a fraction of the larger side.</param>
public sealed record RadialGradientFill(IReadOnlyList<string> Colors, double Cx = 0.5, double Cy = 0.5, double Radius = 0.7) : Fill;

/// <summary>Colour adjustments for pictures. Each value runs from -1 to 1; 0 leaves the image unchanged.</summary>
public sealed record ImageAdjust(double Brightness = 0, double Contrast = 0, double Saturation = 0)
{
    public bool IsIdentity => Brightness == 0 && Contrast == 0 && Saturation == 0;
}

/// <summary>Drop shadow (blurred, offset copy of the layer's shape).</summary>
public sealed record Shadow(string Color = "#59000000", double Blur = 30, double OffsetX = 0, double OffsetY = 18);

// ------------------------------------------------------------------ layers

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ShapeLayer), "shape")]
[JsonDerivedType(typeof(ImageLayer), "image")]
[JsonDerivedType(typeof(DeviceLayer), "device")]
[JsonDerivedType(typeof(TextLayer), "text")]
public abstract class Layer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }

    public double Rotation { get; set; }

    public double Opacity { get; set; } = 1;

    public bool Visible { get; set; } = true;

    public bool Locked { get; set; }

    public Shadow? Shadow { get; set; }
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
}

public sealed class ShapeLayer : Layer
{
    public ShapeKind Shape { get; set; } = ShapeKind.Rectangle;

    public double Radius { get; set; }

    public Fill Fill { get; set; } = new SolidFill("#FFFFFF");

    /// <summary>Gaussian blur radius applied to the shape (soft glows and blobs).</summary>
    public double Blur { get; set; }

    /// <summary>Outline colour; null = no outline.</summary>
    public string? StrokeColor { get; set; }

    public double StrokeWidth { get; set; }
}

public enum ImageFit
{
    Cover,
    Contain,
    Stretch,
}

/// <summary>A picture (screenshot, icon, sticker). <see cref="Source"/> is resolved by an <see cref="IAssetResolver"/>.</summary>
public sealed class ImageLayer : Layer
{
    public string Source { get; set; } = "";

    public ImageFit Fit { get; set; } = ImageFit.Cover;

    public double Radius { get; set; }

    /// <summary>Brightness, contrast and saturation; null = unchanged.</summary>
    public ImageAdjust? Adjust { get; set; }
}

public enum DeviceFrame
{
    /// <summary>No frame: the screenshot with rounded corners.</summary>
    None,

    /// <summary>Generic Android phone with a punch-hole camera.</summary>
    PhonePunchHole,

    /// <summary>Generic modern phone with a pill-shaped camera cut-out (for App Store sets).</summary>
    PhoneIsland,

    /// <summary>Generic tablet.</summary>
    Tablet,
}

/// <summary>A screenshot inside an original, procedurally drawn device frame. W×H is the whole device.</summary>
public sealed class DeviceLayer : Layer
{
    public DeviceFrame Frame { get; set; } = DeviceFrame.PhonePunchHole;

    public string Source { get; set; } = "";

    /// <summary>Body colour of the frame.</summary>
    public string FrameColor { get; set; } = "#FF101114";

    /// <summary>Brightness, contrast and saturation of the screenshot; null = unchanged.</summary>
    public ImageAdjust? Adjust { get; set; }
}

public enum TextAlign
{
    Left,
    Center,
    Right,
}

public enum VerticalAlign
{
    Top,
    Middle,
    Bottom,
}

/// <summary>Wrapped text in a box. Shrinks from <see cref="Size"/> down to <see cref="MinSize"/> to fit, then ellipsizes.</summary>
public sealed class TextLayer : Layer
{
    public string Text { get; set; } = "";

    public string Font { get; set; } = "Poppins";

    public int Weight { get; set; } = 700;

    public double Size { get; set; } = 64;

    public double MinSize { get; set; } = 28;

    public string Color { get; set; } = "#FFFFFFFF";

    public TextAlign Align { get; set; } = TextAlign.Center;

    public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Top;

    public double LineHeight { get; set; } = 1.15;

    public double LetterSpacing { get; set; }

    public int MaxLines { get; set; } = 3;

    /// <summary>Right-to-left text (Arabic, Hebrew): mirrors the alignment. Full shaping arrives with i18n (M7).</summary>
    public bool RightToLeft { get; set; }
}
