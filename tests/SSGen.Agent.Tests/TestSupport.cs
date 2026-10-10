using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using SSGen.Ai;
using SSGen.Core.Data;
using SSGen.Core.Projects;

namespace SSGen.Agent.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ssgen-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public Project NewProject(string name = "P")
    {
        var appDb = new SqliteDatabase(Combine("app.db"), MigrationSet.App);
        appDb.Migrate();
        return new ProjectService(appDb).Create(name, Path);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Deterministic mock LLM: replies come from a function of the request (or a queue).</summary>
public sealed class ScriptedLlm : ILlmProvider
{
    private readonly Func<LlmRequest, string> _reply;

    public ScriptedLlm(Func<LlmRequest, string> reply) => _reply = reply;

    public ScriptedLlm(params string[] replies)
    {
        var queue = new Queue<string>(replies);
        _reply = _ => queue.Count > 0 ? queue.Dequeue() : "{}";
    }

    public List<LlmRequest> Requests { get; } = [];

    public ProviderType Type => ProviderType.OpenAICompatible;

    public string Model => "mock-model";

    public bool IsLocal => true;

    public ProviderCapabilities Capabilities => new(true, false);

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new LlmResponse(_reply(request), new LlmUsage(100, 20)));
    }

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return (await CompleteAsync(request, ct)).Text;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["mock-model"]);

    public Task<bool?> DetectVisionAsync(CancellationToken ct = default) => Task.FromResult<bool?>(true);
}

/// <summary>Captures outgoing HTTP requests and returns canned responses.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _respond;

    public FakeHttp(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) => _respond = respond;

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request, body));
        var (status, json) = _respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
