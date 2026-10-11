using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SSGen.Scene;

namespace SSGen.Templates;

/// <summary>
/// A JSON-defined design, resolution independent: positions and sizes are fractions of the canvas (x, w of the
/// width; y, h of the height; text sizes of the shorter side), colours are literal or tokens ("{brand.primary}",
/// "{text.onBrand}", "{brand.dark@60}" = 60 % opacity), and layers take content by role ("headline",
/// "subheadline", "screenshot", "screenshot2", "icon", "appName").
/// </summary>
public sealed class TemplateDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";

    /// <summary>"screenshot" (default) or "featureGraphic".</summary>
    public string Kind { get; init; } = "screenshot";

    /// <summary>Panorama: the background and decorations run across the whole set (x in set-width units).</summary>
    public bool Panorama { get; init; }

    /// <summary>How many screenshots one image uses (2 for side-by-side designs).</summary>
    public int Screens { get; init; } = 1;

    public required TemplateFill Background { get; init; }

    public IReadOnlyList<TemplateLayer> Layers { get; init; } = [];

    public bool IsFeatureGraphic => string.Equals(Kind, "featureGraphic", StringComparison.OrdinalIgnoreCase);
}

public sealed class TemplateFill
{
    /// <summary>"solid", "linear" or "radial".</summary>
    public string Type { get; init; } = "solid";

    public IReadOnlyList<string> Colors { get; init; } = [];

    public double Angle { get; init; } = 90;

    public double Cx { get; init; } = 0.5;

    public double Cy { get; init; } = 0.5;

    public double Radius { get; init; } = 0.7;
}

public sealed class TemplateLayer
{
    /// <summary>"shape", "text", "device" or "image".</summary>
    public required string Type { get; init; }

    public string? Role { get; init; }

    public string? Name { get; init; }

    // Box (fractions). Devices may instead give Cx + Top/Bottom/Cy + Height (+ MaxWidth).
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public double? Cx { get; init; }
    public double? Cy { get; init; }
    public double? Top { get; init; }
    public double? Bottom { get; init; }
    public double? Height { get; init; }
    public double? MaxWidth { get; init; }

    public double Rotation { get; init; }
    public double Opacity { get; init; } = 1;

    // Shape
    public string? Shape { get; init; }
    public double Radius { get; init; }
    public TemplateFill? Fill { get; init; }
    public double Blur { get; init; }

    // Text (Size/MinSize as fractions of the canvas's shorter side)
    public double Size { get; init; } = 0.07;
    public double MinSize { get; init; } = 0.04;
    public string Font { get; init; } = "Poppins";
    public int Weight { get; init; } = 700;
    public string Color { get; init; } = "{text.onBrand}";
    public string Align { get; init; } = "center";
    public string VerticalAlign { get; init; } = "top";
    public double LineHeight { get; init; } = 1.12;
    public int MaxLines { get; init; } = 3;
    public bool Uppercase { get; init; }

    /// <summary>Letter spacing in ems.</summary>
    public double LetterSpacing { get; init; }

    // Device
    /// <summary>"auto" (from the preset), "phonePunchHole", "phoneIsland", "tablet" or "none".</summary>
    public string Frame { get; init; } = "auto";

    public string FrameColor { get; init; } = "#FF101114";

    public bool Shadow { get; init; } = true;

    // Image
    public string Fit { get; init; } = "cover";
}

/// <summary>The built-in templates (embedded JSON files under Definitions/).</summary>
public static class TemplateLibrary
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly Lazy<IReadOnlyList<TemplateDefinition>> All = new(LoadBuiltIn);

    public static IReadOnlyList<TemplateDefinition> BuiltIn => All.Value;

    public static IEnumerable<TemplateDefinition> Screenshots => BuiltIn.Where(t => !t.IsFeatureGraphic);

    public static TemplateDefinition? Find(string id) => BuiltIn.FirstOrDefault(t => t.Id == id);

    public static TemplateDefinition Parse(string json) =>
        JsonSerializer.Deserialize<TemplateDefinition>(json, Options) ?? throw new JsonException("Empty template.");

    private static List<TemplateDefinition> LoadBuiltIn()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<TemplateDefinition>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("SSGen.Templates.Definitions.", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(Parse(reader.ReadToEnd()));
        }

        return list;
    }
}
