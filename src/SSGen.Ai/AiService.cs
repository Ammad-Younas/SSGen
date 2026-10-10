using SSGen.Ai.Profiles;
using SSGen.Ai.Providers;
using SSGen.Core.Settings;

namespace SSGen.Ai;

/// <summary>A provider ready to use for a task, with its profile and whether it can see screenshots.</summary>
public sealed record ResolvedProvider(ProviderProfile Profile, ILlmProvider Provider, bool Vision)
{
    public bool IsPaid => !Provider.IsLocal;
}

/// <summary>Creates providers from profiles, tests connections, resolves per-task models and vision support.</summary>
public sealed class AiService
{
    /// <summary>1×1 PNG used to probe whether an endpoint/model accepts images.</summary>
    private static readonly byte[] ProbePng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private readonly ProviderProfileStore _profiles;
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;

    public AiService(ProviderProfileStore profiles, ISettingsService settings, HttpClient http)
    {
        _profiles = profiles;
        _settings = settings;
        _http = http;
    }

    public ProviderProfileStore Profiles => _profiles;

    /// <summary>Builds the adapter for a profile. Pass <paramref name="apiKeyOverride"/> to test unsaved keys.</summary>
    public ILlmProvider Create(ProviderProfile profile, string? modelOverride = null, string? apiKeyOverride = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var key = apiKeyOverride ?? _profiles.GetApiKey(profile);
        var model = string.IsNullOrWhiteSpace(modelOverride) ? profile.Model : modelOverride!;
        if (profile.NeedsApiKey && string.IsNullOrWhiteSpace(key))
        {
            throw new LlmException(LlmErrorKind.Authentication, $"No API key saved for \"{profile.Name}\".",
                "Add the key in Settings > AI providers.");
        }

        return profile.Type switch
        {
            ProviderType.Anthropic => new AnthropicProvider(key!, model, profile.BaseUrl),
            ProviderType.OpenAI => new OpenAiProvider(_http, profile.BaseUrl, key, model, compatible: false),
            ProviderType.OpenAICompatible => new OpenAiProvider(_http, profile.EffectiveBaseUrl, key, model, compatible: true),
            ProviderType.Gemini => new GeminiProvider(_http, profile.BaseUrl, key, model),
            ProviderType.Ollama => new OllamaProvider(_http, profile.BaseUrl, model),
            _ => throw new NotSupportedException(profile.Type.ToString()),
        };
    }

    /// <summary>The profile + model for a task: its explicit assignment, else the active profile. Null = no AI configured.</summary>
    public (ProviderProfile Profile, string Model)? ResolveTask(AiTask task)
    {
        var (profileId, model) = _profiles.GetTask(task);
        var profile = _profiles.Get(profileId) ?? _profiles.Get(_settings.Current.ActiveProviderId);
        return profile is null ? null : (profile, string.IsNullOrWhiteSpace(model) ? profile.Model : model!);
    }

    public async Task<ResolvedProvider?> ResolveAsync(AiTask task, CancellationToken ct = default)
    {
        if (ResolveTask(task) is not { } resolved)
        {
            return null;
        }

        var provider = Create(resolved.Profile, resolved.Model);
        var vision = await ResolveVisionAsync(resolved.Profile, provider, ct);
        return new ResolvedProvider(resolved.Profile, provider, vision);
    }

    /// <summary>
    /// Whether to send screenshots: the user's explicit choice wins; otherwise the cached detection, otherwise ask
    /// the provider (Ollama /api/show etc.), otherwise probe with a tiny image. The result is cached on the profile
    /// (only when the profile's own model is being used).
    /// </summary>
    public async Task<bool> ResolveVisionAsync(ProviderProfile profile, ILlmProvider provider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(provider);
        if (profile.VisionMode != VisionMode.Auto)
        {
            return profile.VisionMode == VisionMode.Enabled;
        }

        var sameModel = provider.Model == profile.Model;
        if (sameModel && profile.DetectedVision is { } cached)
        {
            return cached;
        }

        bool detected;
        try
        {
            detected = await provider.DetectVisionAsync(ct) ?? await ProbeVisionAsync(provider, ct);
        }
        catch (LlmException)
        {
            detected = await ProbeVisionAsync(provider, ct);
        }

        if (sameModel)
        {
            _profiles.Save(profile with { DetectedVision = detected });
        }

        return detected;
    }

    private static async Task<bool> ProbeVisionAsync(ILlmProvider provider, CancellationToken ct)
    {
        try
        {
            await provider.CompleteAsync(new LlmRequest
            {
                Messages = [LlmMessage.User("Reply with the single word OK.", new LlmImage(ProbePng))],
                MaxTokens = 16,
            }, ct);
            return true;
        }
        catch (LlmException ex) when (ex.Kind is LlmErrorKind.BadRequest or LlmErrorKind.InvalidOutput)
        {
            return false; // endpoint rejected image input
        }
    }

    /// <summary>"Test connection": list models (where supported), send a tiny request, and detect vision.</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(ProviderProfile profile, string? apiKeyOverride, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        IReadOnlyList<string> models = [];
        try
        {
            var provider = Create(profile, apiKeyOverride: apiKeyOverride);
            try
            {
                models = await provider.ListModelsAsync(ct);
            }
            catch (LlmException ex) when (ex.Kind is LlmErrorKind.ModelNotFound or LlmErrorKind.BadRequest)
            {
                // Some compatible servers don't implement /models; the chat call below is the real test.
            }

            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                return new ConnectionTestResult(true, $"Connected. {models.Count} models available: pick one.", models, null);
            }

            var reply = await provider.CompleteAsync(new LlmRequest { Messages = [LlmMessage.User("Reply with the single word OK.")], MaxTokens = 16 }, ct);
            bool? vision = profile.VisionMode switch
            {
                VisionMode.Enabled => true,
                VisionMode.Disabled => false,
                _ => await provider.DetectVisionAsync(ct) ?? await ProbeVisionAsync(provider, ct),
            };
            var where = provider.IsLocal ? "local" : "cloud";
            return new ConnectionTestResult(true,
                $"Connected to {profile.Model} ({where}). Reply: \"{reply.Text.Trim()}\". Screenshots: {(vision == true ? "supported" : "not supported (UI-tree-only mode)")}.",
                models, vision);
        }
        catch (LlmException ex)
        {
            return new ConnectionTestResult(false, ex.Message + (ex.SuggestedFix is { } f ? " " + f : ""), models, null);
        }
    }
}

/// <summary>
/// Running token/cost total for one run. Local providers are free; paid providers use the user's price table
/// (unknown price => cost shown as unknown, and the cap can't be enforced, which the UI warns about).
/// </summary>
public sealed class CostMeter
{
    private readonly ModelPrice? _price;

    public CostMeter(bool isPaid, ModelPrice? price, decimal? cap)
    {
        IsPaid = isPaid;
        _price = price;
        Cap = isPaid ? cap : null;
    }

    public bool IsPaid { get; }

    public decimal? Cap { get; }

    public bool PriceKnown => !IsPaid || _price is not null;

    public LlmUsage Usage { get; private set; } = LlmUsage.Zero;

    public decimal Cost => IsPaid && _price is not null ? _price.CostOf(Usage) : 0m;

    public bool CapReached => Cap is { } cap && PriceKnown && Cost >= cap;

    public void Add(LlmUsage usage) => Usage += usage;

    public string Describe()
    {
        var tokens = $"{Usage.InputTokens:N0} in / {Usage.OutputTokens:N0} out tokens";
        if (!IsPaid)
        {
            return $"Free (local) · {tokens}";
        }

        if (!PriceKnown)
        {
            return $"{tokens} · cost unknown (add this model's price in Settings)";
        }

        return Cap is { } c ? $"${Cost:0.0000} of ${c:0.00} cap · {tokens}" : $"${Cost:0.0000} · {tokens}";
    }
}
