using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SSGen.Ai.Providers;

/// <summary>Shared plumbing for raw-HTTP providers: JSON posting, error mapping, SSE/NDJSON line streaming.</summary>
public abstract class HttpProviderBase
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    protected HttpProviderBase(HttpClient http, string baseUrl)
    {
        Http = http;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    protected HttpClient Http { get; }

    protected string BaseUrl { get; }

    protected abstract string ProviderName { get; }

    protected abstract void Authorize(HttpRequestMessage request);

    protected async Task<JsonNode> SendJsonAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, HttpCompletionOption.ResponseContentRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new LlmException(LlmErrorKind.Other, $"{ProviderName} returned something that isn't JSON.",
                "Check the base URL points at the API, not a web page. " + Trim(text), ex);
        }
    }

    /// <summary>Streams response lines (SSE "data:" payloads or NDJSON lines, depending on <paramref name="sse"/>).</summary>
    protected async IAsyncEnumerable<string> StreamLinesAsync(string path, JsonNode body, bool sse, [EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, HttpCompletionOption.ResponseHeadersRead, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (sse)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    yield break;
                }

                yield return data;
            }
            else
            {
                yield return line;
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, HttpCompletionOption completion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Authorize(request);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, completion, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorKind.Timeout, $"{ProviderName} didn't answer within {RequestTimeout.TotalMinutes:0} minutes.",
                "Check your connection, or use a faster model.");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException(LlmErrorKind.Network, $"Couldn't reach {(ProviderName.StartsWith("The ", StringComparison.Ordinal) ? "the " + ProviderName[4..] : ProviderName)} at {BaseUrl}.",
                ex.StatusCode is null ? "Check the base URL and your internet connection (or that the local server is running)." : ex.Message, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw LlmException.FromHttp((int)response.StatusCode, ProviderName, ExtractErrorMessage(text));
        }

        return response;
    }

    /// <summary>Pulls a human message out of common error envelopes ({error:{message}}, {error:"..."}, {message}).</summary>
    internal static string ExtractErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var msg = node?["error"]?["message"]?.GetValue<string>()
                      ?? (node?["error"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                      ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg))
            {
                return msg;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }

        return Trim(body);
    }

    protected static string Trim(string s) => s.Length <= 300 ? s.Trim() : s[..300].Trim() + "…";

    protected static bool IsLoopback(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip)));
}
