using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace SSGen.Ai.Providers;

/// <summary>
/// OpenAI Chat Completions, also used for any OpenAI-compatible server (OpenRouter, Groq, Together, LM Studio,
/// llama.cpp…). Compatible servers vary in what they accept, so structured output degrades from json_schema to
/// json_object to prompt-only, remembering what worked.
/// </summary>
public sealed class OpenAiProvider : HttpProviderBase, ILlmProvider
{
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    private readonly string? _apiKey;
    private readonly bool _isOfficial;
    private int _formatLevel; // 0 = json_schema, 1 = json_object, 2 = none

    public OpenAiProvider(HttpClient http, string? baseUrl, string? apiKey, string model, bool compatible)
        : base(http, string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl)
    {
        _apiKey = apiKey;
        Model = model;
        _isOfficial = !compatible;
        Type = compatible ? ProviderType.OpenAICompatible : ProviderType.OpenAI;
    }

    public ProviderType Type { get; }

    public string Model { get; }

    public bool IsLocal => IsLoopback(BaseUrl);

    public ProviderCapabilities Capabilities => new(SupportsStructuredOutput: _formatLevel < 2, SupportsStreaming: true);

    protected override string ProviderName => _isOfficial ? "OpenAI" : "The OpenAI-compatible server";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        while (true)
        {
            try
            {
                var json = await SendJsonAsync(HttpMethod.Post, "/chat/completions", BuildBody(request, stream: false), ct);
                var choice = json["choices"]?[0];
                var text = choice?["message"]?["content"]?.GetValue<string>() ?? "";
                var finish = choice?["finish_reason"]?.GetValue<string>();
                var usage = new LlmUsage(
                    json["usage"]?["prompt_tokens"]?.GetValue<long>() ?? 0,
                    json["usage"]?["completion_tokens"]?.GetValue<long>() ?? 0);
                if (finish == "content_filter")
                {
                    throw new LlmException(LlmErrorKind.Refused, $"{ProviderName} declined this request (content filter).",
                        "The screen may contain content the provider won't process; skip it or use another provider.");
                }

                return new LlmResponse(text, usage, finish);
            }
            catch (LlmException ex) when (ex.Kind == LlmErrorKind.BadRequest && request.JsonSchema is not null && _formatLevel < 2)
            {
                // The server doesn't support this response_format; fall back and remember.
                _formatLevel++;
            }
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var data in StreamLinesAsync("/chat/completions", BuildBody(request with { JsonSchema = null }, stream: true), sse: true, ct))
        {
            var delta = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var json = await SendJsonAsync(HttpMethod.Get, "/models", null, ct);
        return (json["data"] as JsonArray ?? [])
            .Select(m => m?["id"]?.GetValue<string>())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>No reliable capability endpoint: the caller probes with a tiny image when needed.</summary>
    public Task<bool?> DetectVisionAsync(CancellationToken ct = default) => Task.FromResult<bool?>(null);

    protected override void Authorize(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    internal JsonObject BuildBody(LlmRequest request, bool stream)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        }

        foreach (var m in request.Messages)
        {
            JsonNode content;
            if (m.Images is { Count: > 0 } images)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Text } };
                foreach (var img in images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Base64}" },
                    });
                }

                content = parts;
            }
            else
            {
                content = m.Text;
            }

            messages.Add(new JsonObject { ["role"] = m.Role == LlmRole.User ? "user" : "assistant", ["content"] = content });
        }

        var body = new JsonObject { ["model"] = Model, ["messages"] = messages, ["stream"] = stream };
        // Official OpenAI uses max_completion_tokens (required by reasoning models); compatible servers mostly expect max_tokens.
        body[_isOfficial ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens;
        if (request.Temperature is { } t)
        {
            body["temperature"] = t;
        }

        if (request.JsonSchema is { } schema)
        {
            body["response_format"] = _formatLevel switch
            {
                0 => new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = request.SchemaName, ["schema"] = schema.DeepClone(), ["strict"] = true },
                },
                1 => new JsonObject { ["type"] = "json_object" },
                _ => null,
            };
            if (body["response_format"] is null)
            {
                body.Remove("response_format");
            }
        }

        return body;
    }
}
