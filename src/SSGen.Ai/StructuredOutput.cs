using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SSGen.Ai;

/// <summary>Result of a structured request: the parsed value plus total usage across attempts.</summary>
public sealed record StructuredResult<T>(T Value, LlmUsage Usage, int Attempts, string RawText);

/// <summary>
/// Gets schema-conforming JSON from any provider, including weak local models: the schema and a worked example
/// go in the prompt (in addition to native structured output where supported), the reply is parsed tolerantly,
/// validated, and on failure the model is told exactly what was wrong and asked again.
/// </summary>
public static partial class StructuredOutput
{
    /// <summary>The tolerant settings used to read model replies (case-insensitive, snake_case enums); reusable for caches.</summary>
    public static JsonSerializerOptions JsonOptions => Options;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <param name="validate">Returns an error description, or null when the value is acceptable.</param>
    /// <param name="example">A valid example object shown to the model (few-shot).</param>
    public static async Task<StructuredResult<T>> AskAsync<T>(
        ILlmProvider provider,
        LlmRequest request,
        JsonObject schema,
        string example,
        Func<T, string?>? validate = null,
        int maxAttempts = 3,
        Action<LlmUsage>? onUsage = null,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        var usage = LlmUsage.Zero;
        var messages = request.Messages.ToList();
        var system = (request.System is { Length: > 0 } s ? s + "\n\n" : "") + Instructions(schema, example);
        string lastText = "";
        string? lastProblem = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await provider.CompleteAsync(request with { System = system, Messages = messages, JsonSchema = schema }, ct);
            usage += response.Usage;
            onUsage?.Invoke(response.Usage); // count every attempt, including failed ones, toward cost caps
            lastText = response.Text;

            string? problem;
            T? value = null;
            if (TryExtractJson(response.Text) is not { } json)
            {
                problem = "Your reply did not contain a JSON object.";
            }
            else
            {
                try
                {
                    value = JsonSerializer.Deserialize<T>(json, Options);
                    problem = value is null ? "The JSON was empty." : validate?.Invoke(value);
                }
                catch (JsonException ex)
                {
                    problem = "The JSON didn't match the schema: " + ex.Message;
                }
            }

            if (problem is null && value is not null)
            {
                return new StructuredResult<T>(value, usage, attempt, response.Text);
            }

            lastProblem = problem;

            // Append-only correction turn: keep the model's own reply and say what to fix.
            messages.Add(LlmMessage.Assistant(Truncate(response.Text, 2000)));
            messages.Add(LlmMessage.User($"{problem}\nReply again with ONLY one JSON object that follows the schema exactly. No prose, no code fences."));
        }

        throw new LlmException(LlmErrorKind.InvalidOutput,
            $"The model didn't return usable JSON after {maxAttempts} attempts ({lastProblem?.TrimEnd('.')}).",
            "Pick a more capable model for this task in Settings > AI providers. Last reply: " + Truncate(lastText, 200));
    }

    /// <summary>Instructions appended to the system prompt: schema + example + strict format rules.</summary>
    internal static string Instructions(JsonObject schema, string example)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Respond with exactly ONE JSON object and nothing else (no prose, no markdown fences).");
        sb.AppendLine("It must follow this JSON Schema:");
        sb.AppendLine(schema.ToJsonString());
        sb.AppendLine("Example of a valid reply:");
        sb.Append(example);
        return sb.ToString();
    }

    [GeneratedRegex(@"```(?:json)?\s*(?<body>[\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex Fence();

    [GeneratedRegex(@",\s*(?<close>[}\]])")]
    private static partial Regex TrailingComma();

    /// <summary>
    /// Finds the first balanced top-level JSON object in model output: inside ``` fences if present, otherwise the
    /// first {...} span (string-aware brace matching). Trailing commas are removed. Returns null if none parses.
    /// </summary>
    public static string? TryExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var candidates = new List<string>();
        foreach (Match m in Fence().Matches(text))
        {
            candidates.Add(m.Groups["body"].Value);
        }

        candidates.Add(text);
        foreach (var candidate in candidates)
        {
            if (FirstObject(candidate) is not { } obj)
            {
                continue;
            }

            var cleaned = TrailingComma().Replace(obj, "${close}");
            try
            {
                using var _ = JsonDocument.Parse(cleaned, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                return cleaned;
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static string? FirstObject(string s)
    {
        var start = s.IndexOf('{', StringComparison.Ordinal);
        while (start >= 0)
        {
            int depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = start; i < s.Length; i++)
            {
                var c = s[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}' && --depth == 0)
                {
                    return s[start..(i + 1)];
                }
            }

            start = s.IndexOf('{', start + 1);
        }

        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
