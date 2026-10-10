using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace SSGen.Ai.Providers;

/// <summary>
/// Local models through Ollama's native API (/api/chat, /api/tags, /api/show). Nothing leaves the machine
/// unless the base URL points elsewhere.
/// </summary>
public sealed class OllamaProvider : HttpProviderBase, ILlmProvider
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434";

    public OllamaProvider(HttpClient http, string? baseUrl, string model)
        : base(http, string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl)
    {
        Model = model;
    }

    public ProviderType Type => ProviderType.Ollama;

    public string Model { get; }

    public bool IsLocal => IsLoopback(BaseUrl);

    public ProviderCapabilities Capabilities => new(SupportsStructuredOutput: true, SupportsStreaming: true);

    protected override string ProviderName => "Ollama";

    /// <summary>True if an Ollama server answers at the base URL.</summary>
    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            await SendJsonAsync(HttpMethod.Get, "/api/version", null, ct);
            return true;
        }
        catch (LlmException)
        {
            return false;
        }
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = await SendJsonAsync(HttpMethod.Post, "/api/chat", BuildBody(request, stream: false), ct);
        if (json["error"]?.GetValue<string>() is { } error)
        {
            throw new LlmException(LlmErrorKind.BadRequest, "Ollama: " + error, "Check the model name with 'Fetch models'.");
        }

        return new LlmResponse(
            json["message"]?["content"]?.GetValue<string>() ?? "",
            new LlmUsage(json["prompt_eval_count"]?.GetValue<long>() ?? 0, json["eval_count"]?.GetValue<long>() ?? 0),
            json["done_reason"]?.GetValue<string>());
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var line in StreamLinesAsync("/api/chat", BuildBody(request with { JsonSchema = null }, stream: true), sse: false, ct))
        {
            var chunk = JsonNode.Parse(line)?["message"]?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(chunk))
            {
                yield return chunk;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var json = await SendJsonAsync(HttpMethod.Get, "/api/tags", null, ct);
        return (json["models"] as JsonArray ?? [])
            .Select(m => m?["name"]?.GetValue<string>())
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>/api/show lists capabilities such as "vision" for multimodal models.</summary>
    public async Task<bool?> DetectVisionAsync(CancellationToken ct = default)
    {
        var json = await SendJsonAsync(HttpMethod.Post, "/api/show", new JsonObject { ["model"] = Model }, ct);
        if (json["capabilities"] is JsonArray caps)
        {
            return caps.Any(c => c?.GetValue<string>() == "vision");
        }

        return null;
    }

    protected override void Authorize(HttpRequestMessage request)
    {
        // Ollama has no authentication.
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
            var msg = new JsonObject { ["role"] = m.Role == LlmRole.User ? "user" : "assistant", ["content"] = m.Text };
            if (m.Images is { Count: > 0 } images)
            {
                msg["images"] = new JsonArray(images.Select(i => (JsonNode)i.Base64).ToArray());
            }

            messages.Add(msg);
        }

        var options = new JsonObject { ["num_predict"] = request.MaxTokens };
        if (request.Temperature is { } t)
        {
            options["temperature"] = t;
        }

        var body = new JsonObject { ["model"] = Model, ["messages"] = messages, ["stream"] = stream, ["options"] = options };
        if (request.JsonSchema is { } schema)
        {
            body["format"] = schema.DeepClone();
        }

        return body;
    }
}
