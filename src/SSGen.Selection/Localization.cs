using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SSGen.Ai;
using SSGen.Core.Projects;

namespace SSGen.Selection;

/// <summary>A language the store images can be made in. <see cref="Code"/> is a BCP 47 tag.</summary>
public sealed record Language(string Code, string Name, string NativeName, bool RightToLeft = false)
{
    public string Display => NativeName == Name ? $"{Name} ({Code})" : $"{Name} · {NativeName} ({Code})";
}

/// <summary>Common store listing languages, and the project's chosen ones.</summary>
public static class Languages
{
    public static IReadOnlyList<Language> All { get; } =
    [
        new("en-US", "English (US)", "English (US)"),
        new("en-GB", "English (UK)", "English (UK)"),
        new("ar", "Arabic", "العربية", true),
        new("ur", "Urdu", "اردو", true),
        new("fa", "Persian", "فارسی", true),
        new("he", "Hebrew", "עברית", true),
        new("hi", "Hindi", "हिन्दी"),
        new("bn", "Bengali", "বাংলা"),
        new("zh-CN", "Chinese (Simplified)", "简体中文"),
        new("zh-TW", "Chinese (Traditional)", "繁體中文"),
        new("ja", "Japanese", "日本語"),
        new("ko", "Korean", "한국어"),
        new("es-ES", "Spanish (Spain)", "Español (España)"),
        new("es-419", "Spanish (Latin America)", "Español (Latinoamérica)"),
        new("pt-BR", "Portuguese (Brazil)", "Português (Brasil)"),
        new("pt-PT", "Portuguese (Portugal)", "Português (Portugal)"),
        new("fr-FR", "French", "Français"),
        new("de-DE", "German", "Deutsch"),
        new("it-IT", "Italian", "Italiano"),
        new("nl-NL", "Dutch", "Nederlands"),
        new("pl-PL", "Polish", "Polski"),
        new("ru-RU", "Russian", "Русский"),
        new("uk", "Ukrainian", "Українська"),
        new("tr-TR", "Turkish", "Türkçe"),
        new("id", "Indonesian", "Bahasa Indonesia"),
        new("ms", "Malay", "Bahasa Melayu"),
        new("th", "Thai", "ไทย"),
        new("vi", "Vietnamese", "Tiếng Việt"),
        new("sv-SE", "Swedish", "Svenska"),
    ];

    public static Language Find(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
        ?? new Language(code ?? "en-US", code ?? "English", code ?? "English", IsRtlCode(code));

    /// <summary>The project's primary language (captions in the selection table). Default: English (US).</summary>
    public static Language Primary(Project project) => Find(ProjectMeta.Get(project, "design.language") is { Length: > 0 } c ? c : "en-US");

    /// <summary>Additional languages, in the order the user added them.</summary>
    public static IReadOnlyList<Language> Additional(Project project)
    {
        var primary = Primary(project).Code;
        return (ProjectMeta.Get(project, "design.languages") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => !string.Equals(c, primary, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Find)
            .ToList();
    }

    public static void SetPrimary(Project project, string code) => ProjectMeta.Set(project, "design.language", code);

    public static void SetAdditional(Project project, IEnumerable<string> codes) =>
        ProjectMeta.Set(project, "design.languages", string.Join(',', codes));

    private static bool IsRtlCode(string? code) =>
        code?.Split('-')[0].ToLowerInvariant() is "ar" or "ur" or "fa" or "he" or "iw" or "ps" or "sd" or "ug" or "yi" or "dv" or "ku";
}

/// <summary>A caption in one language.</summary>
public sealed record LocalizedCaption(long ScreenId, string Locale, string Headline, string Subheadline, string Source);

/// <summary>Captions in additional languages (project.db captions table).</summary>
public sealed class CaptionStore
{
    private readonly Project _project;

    public CaptionStore(Project project) => _project = project;

    public IReadOnlyDictionary<long, LocalizedCaption> Load(string locale)
    {
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT screen_id, headline, subheadline, source FROM captions WHERE locale = $l;";
        cmd.Parameters.AddWithValue("$l", locale);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<long, LocalizedCaption>();
        while (r.Read())
        {
            result[r.GetInt64(0)] = new LocalizedCaption(r.GetInt64(0), locale, r.GetString(1), r.GetString(2), r.GetString(3));
        }

        return result;
    }

    public void Save(LocalizedCaption caption)
    {
        ArgumentNullException.ThrowIfNull(caption);
        using var c = _project.Database.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO captions(screen_id, locale, headline, subheadline, source, updated_utc) VALUES ($s, $l, $h, $sh, $src, $u)
            ON CONFLICT(screen_id, locale) DO UPDATE SET headline = $h, subheadline = $sh, source = $src, updated_utc = $u;
            """;
        cmd.Parameters.AddWithValue("$s", caption.ScreenId);
        cmd.Parameters.AddWithValue("$l", caption.Locale);
        cmd.Parameters.AddWithValue("$h", caption.Headline);
        cmd.Parameters.AddWithValue("$sh", caption.Subheadline);
        cmd.Parameters.AddWithValue("$src", caption.Source);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}

/// <summary>One caption to translate.</summary>
public sealed record CaptionSource(long ScreenId, string Headline, string Subheadline);

/// <summary>
/// Translates store captions with the configured AI (text only, no screenshots). Keeps them short and
/// marketing-style, leaves the app name untranslated, and validates that every caption came back.
/// </summary>
public sealed class CaptionTranslator
{
    private readonly ILlmProvider _provider;
    private readonly Action<LlmUsage>? _onUsage;

    public CaptionTranslator(ILlmProvider provider, Action<LlmUsage>? onUsage = null)
    {
        _provider = provider;
        _onUsage = onUsage;
    }

    public async Task<IReadOnlyList<LocalizedCaption>> TranslateAsync(IReadOnlyList<CaptionSource> captions, Language from, Language to, string appName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (captions.Count == 0)
        {
            return [];
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Translate these {captions.Count} app store screenshot captions from {from.Name} to {to.Name} ({to.Code}).");
        sb.AppendLine(CultureInfo.InvariantCulture, $"App name (keep it exactly as is): {appName}");
        for (var i = 0; i < captions.Count; i++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. headline: \"{captions[i].Headline}\" | subheadline: \"{captions[i].Subheadline}\"");
        }

        var request = new LlmRequest
        {
            System = """
                You localize App Store and Google Play screenshot captions. Write natural, native marketing language,
                not word-for-word translation. Keep each headline as short as the original (at most about 6 words),
                no trailing period, no quotes, no emojis. Keep app and brand names unchanged. Empty subheadlines stay
                empty. Return one entry per numbered caption, in order.
                """,
            Messages = [LlmMessage.User(sb.ToString())],
            MaxTokens = 1500,
            Temperature = 0.3,
        };

        var result = await StructuredOutput.AskAsync<TranslationReply>(_provider, request, Schema, Example,
            r => r.Captions.Count == captions.Count ? null : $"Return exactly {captions.Count} captions, one per numbered caption, in order.",
            3, _onUsage, ct);

        return captions.Select((c, i) => new LocalizedCaption(
            c.ScreenId,
            to.Code,
            Clean(result.Value.Captions[i].Headline) is { Length: > 0 } h ? h : c.Headline,
            c.Subheadline.Length == 0 ? "" : Clean(result.Value.Captions[i].Subheadline),
            "ai")).ToList();
    }

    private static string Clean(string? s) => (s ?? "").Trim().Trim('"', '“', '”', '«', '»').TrimEnd('.', '。').Trim();

    internal static readonly JsonObject Schema = new()
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
                },
            },
        },
        ["required"] = new JsonArray("captions"),
    };

    internal const string Example = """{"captions":[{"headline":"Organisez vos tâches facilement","subheadline":"Tout au même endroit"}]}""";
}

internal sealed record TranslationReply([property: JsonPropertyName("captions")] IReadOnlyList<Caption> Captions);
