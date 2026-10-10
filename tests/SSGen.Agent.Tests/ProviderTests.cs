using System.Net;
using System.Text.Json.Nodes;
using SSGen.Ai;
using SSGen.Ai.Providers;

namespace SSGen.Agent.Tests;

public class ProviderWireTests
{
    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = new JsonArray("string", "null") } },
        ["additionalProperties"] = false,
    };

    private static LlmRequest Req(bool image = true, bool schema = true) => new()
    {
        System = "sys",
        Messages = [image ? LlmMessage.User("hi", new LlmImage([1, 2, 3], "image/jpeg")) : LlmMessage.User("hi")],
        MaxTokens = 50,
        JsonSchema = schema ? Schema : null,
    };

    [Fact]
    public async Task OpenAi_request_shape_usage_and_format_fallback()
    {
        var http = new FakeHttp((req, body) => body.Contains("json_schema", StringComparison.Ordinal)
            ? (HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema not supported"}}""")
            : (HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"a\":\"x\"}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":4}}"""));
        var p = new OpenAiProvider(new HttpClient(http), "http://127.0.0.1:1234/v1", "k", "m", compatible: true);

        var r = await p.CompleteAsync(Req());

        Assert.Equal("{\"a\":\"x\"}", r.Text);
        Assert.Equal(new LlmUsage(11, 4), r.Usage);
        Assert.True(p.IsLocal);
        Assert.Equal(2, http.Calls.Count); // json_schema rejected -> retried with json_object
        var sent = JsonNode.Parse(http.Calls[1].Body)!;
        Assert.Equal("json_object", sent["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal(50, sent["max_tokens"]!.GetValue<int>()); // compatible servers get max_tokens
        Assert.Equal("system", sent["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", sent["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("Bearer k", http.Calls[0].Request.Headers.Authorization!.ToString());

        var official = new OpenAiProvider(new HttpClient(http), null, "k", "m", compatible: false);
        Assert.Equal(50, official.BuildBody(Req(), false)["max_completion_tokens"]!.GetValue<int>());
        Assert.False(official.IsLocal);
    }

    [Fact]
    public async Task Gemini_request_shape_header_and_usage()
    {
        var http = new FakeHttp((_, _) => (HttpStatusCode.OK,
            """{"candidates":[{"content":{"parts":[{"text":"{\"a\":"},{"text":"null}"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":3}}"""));
        var p = new GeminiProvider(new HttpClient(http), null, "secret", "models/gemini-x");

        var r = await p.CompleteAsync(Req());

        Assert.Equal("{\"a\":null}", r.Text);
        Assert.Equal(new LlmUsage(7, 3), r.Usage);
        var call = http.Calls.Single();
        Assert.EndsWith("/models/gemini-x:generateContent", call.Request.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", call.Request.RequestUri.ToString(), StringComparison.Ordinal); // key not in URL
        Assert.Equal("secret", call.Request.Headers.GetValues("x-goog-api-key").Single());
        var sent = JsonNode.Parse(call.Body)!;
        Assert.Equal("AQID", sent["contents"]![0]!["parts"]![1]!["inline_data"]!["data"]!.GetValue<string>());
        Assert.Equal("sys", sent["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
        var schema = sent["generationConfig"]!["responseSchema"]!;
        Assert.Null(schema["additionalProperties"]);
        Assert.Equal("string", schema["properties"]!["a"]!["type"]!.GetValue<string>());
        Assert.True(schema["properties"]!["a"]!["nullable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Ollama_request_shape_models_and_vision()
    {
        var http = new FakeHttp((req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/api/chat" => (HttpStatusCode.OK, """{"message":{"content":"hello"},"prompt_eval_count":5,"eval_count":2,"done":true}"""),
            "/api/tags" => (HttpStatusCode.OK, """{"models":[{"name":"qwen2.5vl:7b"},{"name":"llama3.2:3b"}]}"""),
            "/api/show" => (HttpStatusCode.OK, body.Contains("qwen", StringComparison.Ordinal)
                ? """{"capabilities":["completion","vision"]}"""
                : """{"capabilities":["completion"]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var p = new OllamaProvider(new HttpClient(http), null, "qwen2.5vl:7b");

        var r = await p.CompleteAsync(Req());
        Assert.Equal("hello", r.Text);
        Assert.Equal(new LlmUsage(5, 2), r.Usage);
        Assert.True(p.IsLocal);
        var sent = JsonNode.Parse(http.Calls[0].Body)!;
        Assert.Equal("AQID", sent["messages"]![1]!["images"]![0]!.GetValue<string>());
        Assert.Equal("object", sent["format"]!["type"]!.GetValue<string>());
        Assert.False(sent["stream"]!.GetValue<bool>());

        Assert.Equal(["llama3.2:3b", "qwen2.5vl:7b"], await p.ListModelsAsync());
        Assert.True(await p.DetectVisionAsync());
        Assert.False(await new OllamaProvider(new HttpClient(http), null, "llama3.2:3b").DetectVisionAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmErrorKind.Authentication, false)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmErrorKind.RateLimited, true)]
    [InlineData((HttpStatusCode)529, LlmErrorKind.Overloaded, true)]
    [InlineData(HttpStatusCode.NotFound, LlmErrorKind.ModelNotFound, false)]
    public async Task Http_errors_are_readable(HttpStatusCode status, LlmErrorKind kind, bool retryable)
    {
        var http = new FakeHttp((_, _) => (status, """{"error":{"message":"details here"}}"""));
        var ex = await Assert.ThrowsAsync<LlmException>(() => new OpenAiProvider(new HttpClient(http), null, "k", "m", false).CompleteAsync(Req(schema: false)));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(retryable, ex.IsRetryable);
        Assert.False(string.IsNullOrWhiteSpace(ex.SuggestedFix));
    }

    [Fact]
    public async Task Unreachable_server_is_a_network_error()
    {
        var p = new OllamaProvider(new HttpClient(), "http://127.0.0.1:9", "m");
        var ex = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(schema: false)));
        Assert.Equal(LlmErrorKind.Network, ex.Kind);
        Assert.False(await p.IsRunningAsync());
    }

    [Fact]
    public void Cost_meter_free_known_unknown_and_cap()
    {
        var local = new CostMeter(isPaid: false, null, 1m);
        local.Add(new LlmUsage(1_000_000, 1_000_000));
        Assert.StartsWith("Free (local)", local.Describe(), StringComparison.Ordinal);
        Assert.False(local.CapReached);

        var paid = new CostMeter(true, new Ai.Profiles.ModelPrice("m", 2m, 10m), 0.05m);
        paid.Add(new LlmUsage(10_000, 2_000)); // 0.02 + 0.02
        Assert.Equal(0.04m, paid.Cost);
        Assert.False(paid.CapReached);
        paid.Add(new LlmUsage(10_000, 0));
        Assert.True(paid.CapReached);

        var unknown = new CostMeter(true, null, 1m);
        Assert.False(unknown.PriceKnown);
        Assert.Contains("cost unknown", unknown.Describe(), StringComparison.Ordinal);
    }
}
