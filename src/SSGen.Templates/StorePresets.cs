using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SSGen.Scene;

namespace SSGen.Templates;

public enum PresetKind
{
    Screenshot,
    FeatureGraphic,
    Icon,
}

/// <summary>Format and size rules from the store's documentation. Preset rules override the store's.</summary>
public sealed record AssetRules
{
    public IReadOnlyList<string>? Formats { get; init; }
    public bool? Alpha { get; init; }
    public int? MinDimension { get; init; }
    public int? MaxDimension { get; init; }

    /// <summary>Longest side divided by shortest side, at most.</summary>
    public double? MaxAspect { get; init; }

    /// <summary>Allowed aspect ratios exactly, e.g. "9:16".</summary>
    public IReadOnlyList<string>? ExactAspects { get; init; }

    public bool? ExactSize { get; init; }
    public long? MaxBytes { get; init; }
    public int? MinCount { get; init; }
    public int? MaxCount { get; init; }

    /// <summary>This rule set with <paramref name="over"/>'s values where it has them.</summary>
    public AssetRules Merge(AssetRules? over) => over is null ? this : new AssetRules
    {
        Formats = over.Formats ?? Formats,
        Alpha = over.Alpha ?? Alpha,
        MinDimension = over.MinDimension ?? MinDimension,
        MaxDimension = over.MaxDimension ?? MaxDimension,
        MaxAspect = over.MaxAspect ?? MaxAspect,
        ExactAspects = over.ExactAspects ?? ExactAspects,
        ExactSize = over.ExactSize ?? ExactSize,
        MaxBytes = over.MaxBytes ?? MaxBytes,
        MinCount = over.MinCount ?? MinCount,
        MaxCount = over.MaxCount ?? MaxCount,
    };
}

public sealed record StorePreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public PresetKind Kind { get; init; } = PresetKind.Screenshot;

    /// <summary>"phone" or "tablet" (screenshots only).</summary>
    public string? Device { get; init; }

    public int Width { get; init; }
    public int Height { get; init; }
    public DeviceFrame Frame { get; init; } = DeviceFrame.PhonePunchHole;
    public bool DefaultSelected { get; init; }
    public bool Required { get; init; }
    public string? RequiredIf { get; init; }
    public IReadOnlyList<string>? AcceptedSizes { get; init; }
    public string? Notes { get; init; }
    public AssetRules? Rules { get; init; }

    /// <summary>Set after loading: the owning store and the merged rules.</summary>
    [JsonIgnore]
    public StoreDefinition Store { get; internal set; } = null!;

    [JsonIgnore]
    /// <summary>A store's general rules are for screenshots; feature graphics and icons have only their own.</summary>
    public AssetRules EffectiveRules => Kind == PresetKind.Screenshot ? Store.Rules.Merge(Rules) : Rules ?? new AssetRules();

    public bool IsTablet => string.Equals(Device, "tablet", StringComparison.OrdinalIgnoreCase);
}

public sealed record StoreDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Source { get; init; }
    public string? Warning { get; init; }
    public AssetRules Rules { get; init; } = new();
    public IReadOnlyList<StorePreset> Presets { get; init; } = [];
}

/// <summary>The verified store specs (see store-presets.json): sources, date, stores and their presets.</summary>
public sealed record StorePresetCatalog
{
    public required string Verified { get; init; }
    public string? Note { get; init; }
    public IReadOnlyList<StoreDefinition> Stores { get; init; } = [];

    [JsonIgnore]
    public IEnumerable<StorePreset> AllPresets => Stores.SelectMany(s => s.Presets);

    /// <summary>Where the catalog was loaded from (embedded default or the user's override file).</summary>
    [JsonIgnore]
    public string Origin { get; private set; } = "built in";

    public StorePreset? Find(string id) => AllPresets.FirstOrDefault(p => p.Id == id);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The user's override file if present and valid, else the built-in catalog.</summary>
    public static StorePresetCatalog Load(string? overrideFile = null)
    {
        if (overrideFile is not null && File.Exists(overrideFile))
        {
            try
            {
                var custom = Parse(File.ReadAllText(overrideFile));
                custom.Origin = overrideFile;
                return custom;
            }
            catch (JsonException)
            {
                // A broken override must not break rendering: fall back to the verified defaults.
            }
        }

        return BuiltIn();
    }

    public static StorePresetCatalog BuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SSGen.Templates.store-presets.json")
                           ?? throw new InvalidOperationException("store-presets.json isn't embedded.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Writes the built-in catalog to <paramref name="path"/> for the user to edit (if it isn't there yet).</summary>
    public static void ExportDefault(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SSGen.Templates.store-presets.json")!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        stream.CopyTo(file);
    }

    public static StorePresetCatalog Parse(string json)
    {
        var catalog = JsonSerializer.Deserialize<StorePresetCatalog>(json, Options) ?? throw new JsonException("Empty preset catalog.");
        foreach (var store in catalog.Stores)
        {
            foreach (var preset in store.Presets)
            {
                preset.Store = store;
            }
        }

        return catalog;
    }
}

/// <summary>One problem found when checking an image against a preset's rules.</summary>
public sealed record ValidationIssue(bool IsError, string Message);

/// <summary>Checks rendered files against the store rules, and set sizes against count limits.</summary>
public static class PresetValidator
{
    public static IReadOnlyList<ValidationIssue> ValidateImage(StorePreset preset, int width, int height, string format, bool hasAlpha, long bytes)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var rules = preset.EffectiveRules;
        var issues = new List<ValidationIssue>();
        var fmt = format.ToLowerInvariant() is "jpg" ? "jpeg" : format.ToLowerInvariant();
        if (rules.Formats is { } formats && !formats.Contains(fmt))
        {
            issues.Add(new(true, $"{preset.Store.Name} accepts {string.Join(" or ", formats).ToUpperInvariant()}, not {fmt.ToUpperInvariant()}."));
        }

        if (rules.Alpha == false && hasAlpha)
        {
            issues.Add(new(true, $"{preset.Store.Name} doesn't accept transparency (alpha channel)."));
        }

        var (min, max) = (Math.Min(width, height), Math.Max(width, height));
        if (rules.MinDimension is { } minDim && min < minDim)
        {
            issues.Add(new(true, $"The shorter side is {min} px; {preset.Store.Name} needs at least {minDim} px."));
        }

        if (rules.MaxDimension is { } maxDim && max > maxDim)
        {
            issues.Add(new(true, $"The longer side is {max} px; {preset.Store.Name} allows at most {maxDim} px."));
        }

        if (rules.MaxAspect is { } aspect && max > min * aspect + 0.5)
        {
            issues.Add(new(true, $"The longer side can be at most {aspect.ToString(CultureInfo.InvariantCulture)} times the shorter side."));
        }

        if (rules.ExactAspects is { Count: > 0 } aspects && !aspects.Any(a => MatchesAspect(a, width, height)))
        {
            issues.Add(new(true, $"The aspect ratio must be {string.Join(" or ", aspects)}."));
        }

        if (rules.ExactSize == true)
        {
            var accepted = preset.AcceptedSizes is { Count: > 0 } sizes ? sizes : [$"{preset.Width}x{preset.Height}"];
            var actual = $"{width}x{height}";
            var rotated = $"{height}x{width}";
            if (!accepted.Contains(actual) && !(preset.Kind == PresetKind.Screenshot && accepted.Contains(rotated)))
            {
                issues.Add(new(true, $"{preset.Name} must be exactly {string.Join(" or ", accepted).Replace('x', '×')} px; this is {width}×{height}."));
            }
        }

        if (rules.MaxBytes is { } maxBytes && bytes > maxBytes)
        {
            issues.Add(new(true, $"The file is {bytes / 1024} KB; the limit is {maxBytes / 1024} KB."));
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateCount(StorePreset preset, int count)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var rules = preset.EffectiveRules;
        var issues = new List<ValidationIssue>();
        if (rules.MinCount is { } min && count < min)
        {
            issues.Add(new(preset.Kind != PresetKind.Screenshot || preset.Required, $"{preset.Store.Name} {preset.Name}: {count} image(s); at least {min} needed."));
        }

        if (rules.MaxCount is { } max && count > max)
        {
            issues.Add(new(true, $"{preset.Store.Name} {preset.Name}: {count} images; at most {max} allowed."));
        }

        return issues;
    }

    private static bool MatchesAspect(string ratio, int width, int height)
    {
        var parts = ratio.Split(':');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        return Math.Abs((width / (double)height) - (a / b)) < 0.003;
    }
}
