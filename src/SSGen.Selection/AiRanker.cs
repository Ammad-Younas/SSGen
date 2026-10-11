using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SkiaSharp;
using SSGen.Ai;
using SSGen.Core.Projects;

namespace SSGen.Selection;

/// <summary>AI results stored in the project, keyed by the images, model and prompt version (re-ranking is free).</summary>
public sealed class VisionCache
{
    private readonly Project _project;

    public VisionCache(Project project) => _project = project;

    public string? Get(string key)
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT response FROM vision_cache WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Put(string key, string response)
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO vision_cache(key, response, created_utc) VALUES ($k, $r, $t) ON CONFLICT(key) DO UPDATE SET response = excluded.response, created_utc = excluded.created_utc;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$r", response);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Ranks screens with an AI model, in batches: scores on core functionality, visual appeal, information richness,
/// uniqueness and conversion value; flags screens to reject; writes a label, a headline (at most ~6 words), a
/// subheadline and the reasoning. Screenshots are downscaled before sending; results are cached. Screens the
/// built-in checks already reject (blank, keyboard, exact duplicates) aren't sent.
/// </summary>
public sealed class AiRanker : IScreenRanker
{
    internal const string PromptVersion = "rank-v1";
    private const int BatchSize = 6;
    private const int MaxImageSide = 640;

    private readonly ILlmProvider _provider;
    private readonly bool _vision;
    private readonly VisionCache? _cache;
    private readonly Action<LlmUsage> _onUsage;

    public AiRanker(ILlmProvider provider, bool vision, VisionCache? cache, Action<LlmUsage> onUsage)
    {
        _provider = provider;
        _vision = vision;
        _cache = cache;
        _onUsage = onUsage;
    }

    public string Name => $"AI ranking ({_provider.Model}{(_vision ? "" : ", text only")})";

    /// <summary>Batches answered from the cache in the last run (no tokens spent).</summary>
    public int CachedBatches { get; private set; }

    public async Task<IReadOnlyList<ScreenAssessment>> RankAsync(IReadOnlyList<ScreenCandidate> candidates, RankingContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(context);
        CachedBatches = 0;

        // Cheap checks first: no tokens for blank or keyboard screens, or exact repeats.
        var baseline = HeuristicRanker.Rank(candidates).ToDictionary(a => a.ScreenId);
        var toSend = candidates.Where(c => baseline[c.Id].Reject is not (RejectReason.Blank or RejectReason.Keyboard)).ToList();

        var results = new Dictionary<long, ScreenAssessment>();
        foreach (var batch in toSend.Chunk(BatchSize))
        {
            foreach (var a in await RankBatchAsync(batch, context, ct))
            {
                results[a.ScreenId] = a;
            }
        }

        var all = candidates.Select(c => results.TryGetValue(c.Id, out var a) ? a : baseline[c.Id]).ToList();
        return HeuristicRanker.MarkDuplicates(candidates, all);
    }

    private async Task<IReadOnlyList<ScreenAssessment>> RankBatchAsync(ScreenCandidate[] batch, RankingContext context, CancellationToken ct)
    {
        var images = _vision ? batch.Select(c => Downscale(File.ReadAllBytes(c.FullPath))).ToList() : [];
        var key = CacheKey(batch, images, context);
        RankReply? reply = null;
        if (_cache?.Get(key) is { } cached)
        {
            reply = JsonSerializer.Deserialize<RankReply>(cached, StructuredOutput.JsonOptions);
            CachedBatches++;
        }

        if (reply is null)
        {
            var request = new LlmRequest
            {
                System = SystemPrompt,
                Messages = [LlmMessage.User(BatchPrompt(batch, context), [.. images.Select(b => new LlmImage(b, "image/jpeg"))])],
                MaxTokens = 2500,
                Temperature = 0.3,
            };
            var result = await StructuredOutput.AskAsync<RankReply>(_provider, request, Schema, Example, r => Validate(r, batch.Length), maxAttempts: 3, _onUsage, ct);
            reply = result.Value;
            _cache?.Put(key, JsonSerializer.Serialize(reply, StructuredOutput.JsonOptions));
        }

        return reply.Screens.Where(s => s.Screen >= 1 && s.Screen <= batch.Length).Select(s => ToAssessment(s, batch[s.Screen - 1])).ToList();
    }

    internal static ScreenAssessment ToAssessment(RankedScreen s, ScreenCandidate c)
    {
        static double C(double v) => Math.Clamp(v, 0, 10);
        var score = ((C(s.Core) * 0.30) + (C(s.Visual) * 0.20) + (C(s.Richness) * 0.20) + (C(s.Uniqueness) * 0.10) + (C(s.Conversion) * 0.20)) * 10;
        var reject = string.IsNullOrWhiteSpace(s.Reject) || s.Reject is "none" or "null" ? null : s.Reject.Trim().ToLowerInvariant();
        var label = string.IsNullOrWhiteSpace(s.Label) ? c.DisplayName : s.Label.Trim();
        var role = s.Settings ? StoryRole.Settings : StoryRole.Feature;
        return new ScreenAssessment(c.Id, Math.Round(score, 1), reject, label, ClampWords(s.Headline ?? label, 8), (s.Subheadline ?? "").Trim(), (s.Reasoning ?? "").Trim(), role, "ai") { Group = c.Screen.Activity };
    }

    private static string ClampWords(string text, int max)
    {
        var words = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= max ? string.Join(' ', words) : string.Join(' ', words.Take(max));
    }

    // ------------------------------------------------------------------ story polish

    /// <summary>
    /// Text-only pass over the chosen set: distinct, consistent headlines (at most ~6 words) and subheadlines,
    /// written as one story. Order is kept. Falls back to the input on unusable output.
    /// </summary>
    public async Task<IReadOnlyList<ScreenAssessment>> PolishAsync(IReadOnlyList<ScreenAssessment> picked, RankingContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(context);
        if (picked.Count == 0)
        {
            return picked;
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"App: {context.AppName}.");
        if (!string.IsNullOrWhiteSpace(context.Hints))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"About the app (from the user): {context.Hints}");
        }

        sb.AppendLine("These screenshots will appear in this order on the store listing. Rewrite their captions as one story:");
        for (var i = 0; i < picked.Count; i++)
        {
            var a = picked[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. {a.Label}: headline \"{a.Headline}\", subheadline \"{a.Subheadline}\". {a.Reasoning}");
        }

        var request = new LlmRequest
        {
            System = """
                You write App Store and Google Play screenshot captions. Each headline: at most 6 words, benefit-led,
                no trailing period, no emojis, no claims you can't see (no "#1", no prices). Subheadline: one short line
                that supports the headline, or empty. Every headline must be different. The first one sells the app's
                main value.
                """,
            Messages = [LlmMessage.User(sb.ToString())],
            MaxTokens = 1200,
            Temperature = 0.5,
        };

        try
        {
            var result = await StructuredOutput.AskAsync<PolishReply>(_provider, request, PolishSchema, PolishExample,
                r => r.Captions.Count == picked.Count ? null : $"Return exactly {picked.Count} captions, one per screenshot, in order.", 3, _onUsage, ct);
            return picked.Select((a, i) => a with
            {
                Headline = ClampWords(result.Value.Captions[i].Headline ?? a.Headline, 8),
                Subheadline = (result.Value.Captions[i].Subheadline ?? a.Subheadline).Trim(),
            }).ToList();
        }
        catch (LlmException ex) when (ex.Kind is LlmErrorKind.InvalidOutput or LlmErrorKind.Refused)
        {
            return picked;
        }
    }

    // ------------------------------------------------------------------ prompts and schema

    internal const string SystemPrompt = """
        You choose screenshots for an app's Google Play and App Store listing. For each numbered screenshot, score
        0 to 10:
        - core: shows the app's main functionality (what people install it for)
        - visual: looks appealing (clean, colourful, polished)
        - richness: full of real-looking content (not empty states, not placeholders)
        - uniqueness: different from the other screenshots
        - conversion: would make someone install the app
        Set "reject" when a screenshot must not be used: "blank", "loading", "crash", "permission" (system dialog),
        "login" (sign-in or sign-up screen), "keyboard" (keyboard covers it), "sensitive" (shows personal data such
        as real names, emails, phone numbers), "error" (error or empty state). Otherwise null.
        "settings": true for settings, about, help or legal screens (they go last).
        "label": 1 to 4 words naming the screen. "headline": a marketing headline of at most 6 words. "subheadline":
        one short supporting line, or empty. "reasoning": one sentence.
        """;

    private string BatchPrompt(ScreenCandidate[] batch, RankingContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"App: {context.AppName}. The user wants about {context.TargetCount} screenshots in the listing.");
        if (!string.IsNullOrWhiteSpace(context.Hints))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"About the app (from the user): {context.Hints}");
        }

        sb.AppendLine(_vision ? "The screenshots are attached in this order:" : "No images are available; judge from the screen names and visible text:");
        for (var i = 0; i < batch.Length; i++)
        {
            var c = batch[i];
            var text = string.Join(" | ", (c.Screen.UiText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(15));
            sb.AppendLine(CultureInfo.InvariantCulture, $"Screenshot {i + 1}: {c.DisplayName} (activity {c.Screen.Activity ?? "unknown"}){(text.Length > 0 ? ". Visible text: " + text : "")}");
        }

        return sb.ToString();
    }

    internal static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["screens"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["screen"] = new JsonObject { ["type"] = "integer" },
                        ["core"] = new JsonObject { ["type"] = "number" },
                        ["visual"] = new JsonObject { ["type"] = "number" },
                        ["richness"] = new JsonObject { ["type"] = "number" },
                        ["uniqueness"] = new JsonObject { ["type"] = "number" },
                        ["conversion"] = new JsonObject { ["type"] = "number" },
                        ["reject"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                        ["settings"] = new JsonObject { ["type"] = "boolean" },
                        ["label"] = new JsonObject { ["type"] = "string" },
                        ["headline"] = new JsonObject { ["type"] = "string" },
                        ["subheadline"] = new JsonObject { ["type"] = "string" },
                        ["reasoning"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("screen", "core", "visual", "richness", "uniqueness", "conversion", "reject", "settings", "label", "headline", "subheadline", "reasoning"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("screens"),
        ["additionalProperties"] = false,
    };

    internal const string Example = """
        {"screens":[{"screen":1,"core":9,"visual":8,"richness":8,"uniqueness":7,"conversion":9,"reject":null,"settings":false,"label":"Notes list","headline":"All your notes, one tap away","subheadline":"Lists, sketches and voice memos","reasoning":"Shows the main list full of real content."}]}
        """;

    internal static readonly JsonObject PolishSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["captions"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["headline"] = new JsonObject { ["type"] = "string" },
                        ["subheadline"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("headline", "subheadline"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("captions"),
        ["additionalProperties"] = false,
    };

    internal const string PolishExample = """{"captions":[{"headline":"Capture ideas the moment they strike","subheadline":"Notes, lists and sketches in one place"}]}""";

    internal static string? Validate(RankReply r, int count)
    {
        var numbers = r.Screens.Select(s => s.Screen).ToHashSet();
        var missing = Enumerable.Range(1, count).Where(i => !numbers.Contains(i)).ToList();
        return missing.Count > 0 ? $"Missing screenshots: {string.Join(", ", missing)}. Return one entry per screenshot (1 to {count})." : null;
    }

    private string CacheKey(ScreenCandidate[] batch, List<byte[]> images, RankingContext context)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder($"{PromptVersion}|{_provider.Type}|{_provider.Model}|{_vision}|{context.AppName}|{context.Hints}|{context.TargetCount}");
        for (var i = 0; i < batch.Length; i++)
        {
            var bytes = images.Count > i ? images[i] : Encoding.UTF8.GetBytes($"{batch[i].Screen.Activity}|{batch[i].Screen.Title}|{batch[i].Screen.UiText}");
            sb.Append('|').Append(Convert.ToHexString(SHA256.HashData(bytes)));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>JPEG, at most 640 px on the long side: enough to judge a screen, few tokens.</summary>
    internal static byte[] Downscale(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded);
        if (bitmap is null)
        {
            return encoded;
        }

        var scale = Math.Min(1f, (float)MaxImageSide / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = bitmap.Resize(new SKImageInfo(Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}

internal sealed record RankReply([property: JsonPropertyName("screens")] IReadOnlyList<RankedScreen> Screens);

internal sealed record RankedScreen(
    [property: JsonPropertyName("screen")] int Screen,
    [property: JsonPropertyName("core")] double Core,
    [property: JsonPropertyName("visual")] double Visual,
    [property: JsonPropertyName("richness")] double Richness,
    [property: JsonPropertyName("uniqueness")] double Uniqueness,
    [property: JsonPropertyName("conversion")] double Conversion,
    [property: JsonPropertyName("reject")] string? Reject,
    [property: JsonPropertyName("settings")] bool Settings,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("headline")] string? Headline,
    [property: JsonPropertyName("subheadline")] string? Subheadline,
    [property: JsonPropertyName("reasoning")] string? Reasoning);

internal sealed record PolishReply([property: JsonPropertyName("captions")] IReadOnlyList<Caption> Captions);

internal sealed record Caption(
    [property: JsonPropertyName("headline")] string? Headline,
    [property: JsonPropertyName("subheadline")] string? Subheadline);
