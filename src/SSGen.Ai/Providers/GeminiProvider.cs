using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace SSGen.Ai.Providers;

/// <summary>
/// Google Gemini via the native generateContent API (v1beta). The API key goes in the x-goog-api-key header,
/// never the URL, so it can't leak into logs.
/// </summary>
public sealed class GeminiProvider : HttpProviderBase, ILlmProvider
{
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly string? _apiKey;
    private bool _schemaSupported = true;

    public GeminiProvider(HttpClient http, string? baseUrl, string? apiKey, string model)
        : base(http, string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl)
    {
        _apiKey = apiKey;
        Model = model.StartsWith("models/", StringComparison.Ordinal) ? model["models/".Length..] : model;
    }

    public ProviderType Type => ProviderType.Gemini;

    public string Model { get; }

    public bool IsLocal => false;

    public ProviderCapabilities Capabilities => new(SupportsStructuredOutput: true, SupportsStreaming: true);

    protected override string ProviderName => "Gemini";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        while (true)
        {
            try
            {
                var json = await SendJsonAsync(HttpMethod.Post, $"/models/{Model}:generateContent", BuildBody(request), ct);
                var candidate = json["candidates"]?[0];
                var finish = candidate?["finishReason"]?.GetValue<string>();
                if (candidate is null && json["promptFeedback"]?["blockReason"] is { } block)
                {
                    throw new LlmException(LlmErrorKind.Refused, $"Gemini blocked the request ({block}).",
                        "The screen may contain content Gemini won't process; skip it or use another provider.");
                }

                var text = string.Concat((candidate?["content"]?["parts"] as JsonArray ?? [])
                    .Select(p => p?["text"]?.GetValue<string>()).OfType<string>());
                var usage = json["usageMetadata"];
                return new LlmResponse(text, new LlmUsage(
                    usage?["promptTokenCount"]?.GetValue<long>() ?? 0,
                    usage?["candidatesTokenCount"]?.GetValue<long>() ?? 0), finish);
            }
            catch (LlmException ex) when (ex.Kind == LlmErrorKind.BadRequest && request.JsonSchema is not null && _schemaSupported)
            {
                // Gemini's responseSchema accepts an OpenAPI subset; if ours is rejected, keep JSON mode without it.
                _schemaSupported = false;
            }
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var data in StreamLinesAsync($"/models/{Model}:streamGenerateContent?alt=sse", BuildBody(request with { JsonSchema = null }), sse: true, ct))
        {
            var parts = JsonNode.Parse(data)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            foreach (var p in parts ?? [])
            {
                if (p?["text"]?.GetValue<string>() is { Length: > 0 } t)
                {
                    yield return t;
                }
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var json = await SendJsonAsync(HttpMethod.Get, "/models?pageSize=1000", null, ct);
        return (json["models"] as JsonArray ?? [])
            .Where(m => (m?["supportedGenerationMethods"] as JsonArray)?.Any(x => x?.GetValue<string>() == "generateContent") ?? true)
            .Select(m => m?["name"]?.GetValue<string>())
            .OfType<string>()
            .Select(n => n.StartsWith("models/", StringComparison.Ordinal) ? n["models/".Length..] : n)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Gemini chat models accept images.</summary>
    public Task<bool?> DetectVisionAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);

    protected override void Authorize(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Add("x-goog-api-key", _apiKey);
        }
    }

    internal JsonObject BuildBody(LlmRequest request)
    {
        var contents = new JsonArray();
        foreach (var m in request.Messages)
        {
            var parts = new JsonArray { new JsonObject { ["text"] = m.Text } };
            foreach (var img in m.Images ?? [])
            {
                parts.Add(new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = img.MediaType, ["data"] = img.Base64 } });
            }

            contents.Add(new JsonObject { ["role"] = m.Role == LlmRole.User ? "user" : "model", ["parts"] = parts });
        }

        var config = new JsonObject { ["maxOutputTokens"] = request.MaxTokens };
        if (request.Temperature is { } t)
        {
            config["temperature"] = t;
        }

        if (request.JsonSchema is { } schema)
        {
            config["responseMimeType"] = "application/json";
            if (_schemaSupported)
            {
                config["responseSchema"] = ToGeminiSchema(schema);
            }
        }

        var body = new JsonObject { ["contents"] = contents, ["generationConfig"] = config };
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = request.System } } };
        }

        return body;
    }

    /// <summary>Gemini's responseSchema is an OpenAPI subset: drop keywords it rejects (additionalProperties, $schema…).</summary>
    internal static JsonNode ToGeminiSchema(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (k, v) in obj)
                {
                    if (k is "additionalProperties" or "$schema" or "$id" or "title" or "default")
                    {
                        continue;
                    }

                    // JSON Schema ["string","null"] -> OpenAPI { type: "string", nullable: true }
                    if (k == "type" && v is JsonArray types)
                    {
                        var names = types.Select(t => t?.GetValue<string>()).OfType<string>().ToList();
                        copy["type"] = names.FirstOrDefault(n => n != "null") ?? "string";
                        if (names.Contains("null"))
                        {
                            copy["nullable"] = true;
                        }

                        continue;
                    }

                    copy[k] = v is null ? null : ToGeminiSchema(v);
                }

                return copy;
            case JsonArray arr:
                return new JsonArray(arr.Select(x => x is null ? null : ToGeminiSchema(x)).ToArray());
            default:
                return node.DeepClone();
        }
    }
}
