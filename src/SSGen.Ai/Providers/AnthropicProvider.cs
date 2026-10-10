using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace SSGen.Ai.Providers;

/// <summary>Claude through the official Anthropic C# SDK (Messages API).</summary>
public sealed class AnthropicProvider : ILlmProvider
{
    /// <summary>Pre-filled in new Anthropic profiles; the user can pick any model from 'Fetch models'.</summary>
    public const string SuggestedModel = "claude-opus-5-5";

    private readonly AnthropicClient _client;
    private bool _structuredSupported = true;

    public AnthropicProvider(string apiKey, string model, string? baseUrl = null)
    {
        Model = model;
        _client = string.IsNullOrWhiteSpace(baseUrl)
            ? new AnthropicClient { ApiKey = apiKey }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = baseUrl };
    }

    public ProviderType Type => ProviderType.Anthropic;

    public string Model { get; }

    public bool IsLocal => false;

    public ProviderCapabilities Capabilities => new(SupportsStructuredOutput: _structuredSupported, SupportsStreaming: true);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        while (true)
        {
            try
            {
                var response = await Call(() => _client.Messages.Create(BuildParams(request), ct));
                if (response.StopReason == "refusal")
                {
                    throw new LlmException(LlmErrorKind.Refused, "Claude declined to process this request.",
                        "The screen may contain content Claude won't analyse; skip it or continue manually.");
                }

                var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
                return new LlmResponse(text, new LlmUsage(response.Usage.InputTokens, response.Usage.OutputTokens), response.StopReason?.ToString());
            }
            catch (LlmException ex) when (ex.Kind == LlmErrorKind.BadRequest && request.JsonSchema is not null && _structuredSupported)
            {
                // Older models don't support output_config.format; StructuredOutput still prompts + parses tolerantly.
                _structuredSupported = false;
            }
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stream = _client.Messages.CreateStreaming(BuildParams(request with { JsonSchema = null }), ct);
        await using var e = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            RawMessageStreamEvent ev;
            try
            {
                if (!await e.MoveNextAsync())
                {
                    yield break;
                }

                ev = e.Current;
            }
            catch (Exception ex) when (Map(ex) is { } mapped)
            {
                throw mapped;
            }

            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                yield return text.Text;
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var page = await Call(() => _client.Models.List(cancellationToken: ct));
        return page.Items.Select(m => m.ID).ToList();
    }

    /// <summary>All current Claude models accept image input.</summary>
    public Task<bool?> DetectVisionAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);

    private MessageCreateParams BuildParams(LlmRequest request)
    {
        var messages = new List<MessageParam>();
        foreach (var m in request.Messages)
        {
            if (m.Images is { Count: > 0 } images)
            {
                var blocks = new List<ContentBlockParam>();
                foreach (var img in images)
                {
                    blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = img.Base64, MediaType = img.MediaType } });
                }

                blocks.Add(new TextBlockParam { Text = m.Text });
                messages.Add(new MessageParam { Role = m.Role == LlmRole.User ? Role.User : Role.Assistant, Content = blocks });
            }
            else
            {
                messages.Add(new MessageParam { Role = m.Role == LlmRole.User ? Role.User : Role.Assistant, Content = m.Text });
            }
        }

        var p = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = request.MaxTokens,
            Messages = messages,
        };
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            p = p with { System = request.System };
        }

        if (request.JsonSchema is { } schema && _structuredSupported)
        {
            p = p with { OutputConfig = new OutputConfig { Format = new JsonOutputFormat { Schema = ToSchemaDictionary(schema) } } };
        }

        return p;
    }

    internal static Dictionary<string, JsonElement> ToSchemaDictionary(JsonObject schema) =>
        schema.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Most-specific-first mapping of the SDK's typed exceptions.</summary>
    private static LlmException? Map(Exception ex) => ex switch
    {
        LlmException => null,
        AnthropicUnauthorizedException or AnthropicForbiddenException => LlmException.FromHttp(401, "Anthropic", ex.Message),
        AnthropicNotFoundException => LlmException.FromHttp(404, "Anthropic", ex.Message),
        AnthropicRateLimitException => LlmException.FromHttp(429, "Anthropic", ex.Message),
        AnthropicBadRequestException or AnthropicUnprocessableEntityException => LlmException.FromHttp(400, "Anthropic", ex.Message),
        Anthropic5xxException => LlmException.FromHttp(529, "Anthropic", ex.Message),
        AnthropicIOException => new LlmException(LlmErrorKind.Network, "Couldn't reach the Anthropic API.", "Check your internet connection.", ex),
        AnthropicApiException => LlmException.FromHttp(400, "Anthropic", ex.Message),
        TaskCanceledException or OperationCanceledException => null,
        _ => null,
    };
}
