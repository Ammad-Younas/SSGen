using System.Text.Json.Nodes;
using SSGen.Core;

namespace SSGen.Ai;

/// <summary>Which wire protocol a provider profile speaks.</summary>
public enum ProviderType
{
    Anthropic,
    OpenAI,

    /// <summary>Any server implementing OpenAI's Chat Completions API (OpenRouter, Groq, Together, LM Studio, llama.cpp…).</summary>
    OpenAICompatible,
    Gemini,
    Ollama,
}

/// <summary>Whether to send screenshots. Auto = detect (and cache) from the provider/model.</summary>
public enum VisionMode
{
    Auto,
    Enabled,
    Disabled,
}

public enum LlmRole
{
    User,
    Assistant,
}

public sealed record LlmImage(byte[] Data, string MediaType = "image/png")
{
    public string Base64 => Convert.ToBase64String(Data);
}

public sealed record LlmMessage(LlmRole Role, string Text, IReadOnlyList<LlmImage>? Images = null)
{
    public static LlmMessage User(string text, params LlmImage[] images) => new(LlmRole.User, text, images.Length == 0 ? null : images);

    public static LlmMessage Assistant(string text) => new(LlmRole.Assistant, text);
}

public sealed record LlmRequest
{
    public string? System { get; init; }

    public required IReadOnlyList<LlmMessage> Messages { get; init; }

    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Optional JSON Schema (an object schema) the response must follow. Providers use native structured output
    /// where available; <see cref="StructuredOutput"/> additionally prompts, parses tolerantly and retries.
    /// </summary>
    public JsonObject? JsonSchema { get; init; }

    public string SchemaName { get; init; } = "response";

    public double? Temperature { get; init; }

    public bool HasImages => Messages.Any(m => m.Images is { Count: > 0 });
}

public sealed record LlmUsage(long InputTokens, long OutputTokens)
{
    public static readonly LlmUsage Zero = new(0, 0);

    public static LlmUsage operator +(LlmUsage a, LlmUsage b) =>
        new((a ?? Zero).InputTokens + (b ?? Zero).InputTokens, (a ?? Zero).OutputTokens + (b ?? Zero).OutputTokens);

    public LlmUsage Add(LlmUsage other) => this + other;
}

public sealed record LlmResponse(string Text, LlmUsage Usage, string? StopReason = null);

public sealed record ConnectionTestResult(bool Success, string Message, IReadOnlyList<string> Models, bool? SupportsVision);

public enum LlmErrorKind
{
    Authentication,
    RateLimited,
    Overloaded,
    BadRequest,
    ModelNotFound,
    Network,
    Timeout,
    Refused,
    InvalidOutput,
    Other,
}

/// <summary>A provider failure with a readable message, a suggested fix, and whether retrying may help.</summary>
public sealed class LlmException : UserFacingException
{
    public LlmException() { }

    public LlmException(string message) : base(message) { }

    public LlmException(string message, Exception inner) : base(message, inner) { }

    public LlmException(LlmErrorKind kind, string message, string? fix = null, Exception? inner = null)
        : base(message, fix, inner)
    {
        Kind = kind;
    }

    public LlmErrorKind Kind { get; } = LlmErrorKind.Other;

    public bool IsRetryable => Kind is LlmErrorKind.RateLimited or LlmErrorKind.Overloaded or LlmErrorKind.Network or LlmErrorKind.Timeout;

    /// <summary>Maps an HTTP status + provider error text to an exception with a helpful fix.</summary>
    public static LlmException FromHttp(int status, string providerName, string body)
    {
        var detail = body.Length > 300 ? body[..300] + "…" : body;
        return status switch
        {
            401 or 403 => new(LlmErrorKind.Authentication, $"{providerName} rejected the API key ({status}).",
                "Check the key in Settings > AI providers. It may be mistyped, revoked, or lack access to this model."),
            404 => new(LlmErrorKind.ModelNotFound, $"{providerName} doesn't know that model or endpoint (404).",
                "Use 'Fetch models' in the provider settings to pick an available model, and check the base URL. " + detail),
            408 => new(LlmErrorKind.Timeout, $"{providerName} timed out.", "Try again; if it keeps happening, use a faster model."),
            429 => new(LlmErrorKind.RateLimited, $"{providerName} rate limit reached (429).",
                "Wait a minute and resume, or lower the request rate. Your plan may have a low limit."),
            529 or 503 => new(LlmErrorKind.Overloaded, $"{providerName} is overloaded right now ({status}).", "Try again in a few minutes."),
            >= 500 => new(LlmErrorKind.Overloaded, $"{providerName} had a server error ({status}).", "Try again shortly. " + detail),
            _ => new(LlmErrorKind.BadRequest, $"{providerName} rejected the request ({status}).", detail),
        };
    }
}

public sealed record ProviderCapabilities(bool SupportsStructuredOutput, bool SupportsStreaming);

/// <summary>
/// One configured model endpoint. Implementations translate to a vendor's wire protocol and throw
/// <see cref="LlmException"/> for every failure.
/// </summary>
public interface ILlmProvider
{
    ProviderType Type { get; }

    string Model { get; }

    /// <summary>True for providers running on this machine (no data leaves it, no cost).</summary>
    bool IsLocal { get; }

    ProviderCapabilities Capabilities { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    IAsyncEnumerable<string> StreamAsync(LlmRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Best-effort vision detection for <see cref="Model"/>; null if unknown (the caller may probe).</summary>
    Task<bool?> DetectVisionAsync(CancellationToken ct = default);
}
