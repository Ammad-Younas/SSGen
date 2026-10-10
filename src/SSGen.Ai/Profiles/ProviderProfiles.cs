using System.Globalization;
using SSGen.Ai.Providers;
using SSGen.Core.Data;
using SSGen.Core.Security;

namespace SSGen.Ai.Profiles;

/// <summary>AI tasks that can each use a different profile/model.</summary>
public enum AiTask
{
    Exploration,
    Ranking,
    Headlines,
    Translation,
}

/// <summary>A configured provider endpoint. The API key is stored separately (DPAPI) under <see cref="SecretName"/>.</summary>
public sealed record ProviderProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required ProviderType Type { get; init; }
    public string? BaseUrl { get; init; }
    public required string Model { get; init; }
    public VisionMode VisionMode { get; init; } = VisionMode.Auto;
    public bool? DetectedVision { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public string SecretName => "provider:" + Id;

    public string TypeName => Type switch
    {
        ProviderType.OpenAICompatible => "OpenAI-compatible",
        ProviderType.Ollama => "Ollama (local)",
        _ => Type.ToString(),
    };

    public bool NeedsApiKey => Type is not ProviderType.Ollama and not ProviderType.OpenAICompatible;

    /// <summary>Base URL the provider will actually use (profile value or the vendor default).</summary>
    public string EffectiveBaseUrl => !string.IsNullOrWhiteSpace(BaseUrl) ? BaseUrl! : DefaultBaseUrl(Type);

    public static string DefaultBaseUrl(ProviderType type) => type switch
    {
        ProviderType.Anthropic => "https://api.anthropic.com",
        ProviderType.OpenAI => OpenAiProvider.DefaultBaseUrl,
        ProviderType.Gemini => GeminiProvider.DefaultBaseUrl,
        ProviderType.Ollama => OllamaProvider.DefaultBaseUrl,
        _ => "http://127.0.0.1:1234/v1", // LM Studio's default; OpenRouter/Groq/etc. users replace it
    };

    public override string ToString() => $"{Name} ({Type}, {Model})";
}

/// <summary>Stores provider profiles, per-task assignments and the user's price table in the app database.</summary>
public sealed class ProviderProfileStore
{
    private readonly SqliteDatabase _db;
    private readonly ISecretStore _secrets;

    public ProviderProfileStore(SqliteDatabase appDatabase, ISecretStore secrets)
    {
        _db = appDatabase;
        _secrets = secrets;
    }

    public IReadOnlyList<ProviderProfile> List()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, type, base_url, model, vision_mode, detected_vision, created_utc FROM provider_profiles ORDER BY created_utc;";
        using var r = cmd.ExecuteReader();
        var list = new List<ProviderProfile>();
        while (r.Read())
        {
            list.Add(new ProviderProfile
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                Type = Enum.Parse<ProviderType>(r.GetString(2)),
                BaseUrl = r.IsDBNull(3) ? null : r.GetString(3),
                Model = r.GetString(4),
                VisionMode = Enum.TryParse<VisionMode>(r.GetString(5), out var vm) ? vm : VisionMode.Auto,
                DetectedVision = r.IsDBNull(6) ? null : r.GetInt64(6) != 0,
                CreatedUtc = DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            });
        }

        return list;
    }

    public ProviderProfile? Get(string? id) => id is null ? null : List().FirstOrDefault(p => p.Id == id);

    /// <summary>Inserts or updates. A null <paramref name="apiKey"/> keeps the stored key; empty removes it.</summary>
    public void Save(ProviderProfile profile, string? apiKey = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using (var c = _db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO provider_profiles(id, name, type, base_url, model, vision_mode, detected_vision, created_utc)
                VALUES ($id, $n, $t, $b, $m, $v, $d, $c)
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, type = excluded.type, base_url = excluded.base_url,
                    model = excluded.model, vision_mode = excluded.vision_mode, detected_vision = excluded.detected_vision;
                """;
            cmd.Parameters.AddWithValue("$id", profile.Id);
            cmd.Parameters.AddWithValue("$n", profile.Name);
            cmd.Parameters.AddWithValue("$t", profile.Type.ToString());
            cmd.Parameters.AddWithValue("$b", (object?)profile.BaseUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$m", profile.Model);
            cmd.Parameters.AddWithValue("$v", profile.VisionMode.ToString());
            cmd.Parameters.AddWithValue("$d", profile.DetectedVision is { } d ? (d ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("$c", profile.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        if (apiKey is not null)
        {
            if (apiKey.Length == 0)
            {
                _secrets.Delete(profile.SecretName);
            }
            else
            {
                _secrets.Save(profile.SecretName, apiKey);
            }
        }
    }

    public void Delete(string id)
    {
        using (var c = _db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM provider_profiles WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        _secrets.Delete("provider:" + id);
    }

    public string? GetApiKey(ProviderProfile profile) => _secrets.Load((profile ?? throw new ArgumentNullException(nameof(profile))).SecretName);

    public bool HasApiKey(ProviderProfile profile) => _secrets.ListNames().Contains((profile ?? throw new ArgumentNullException(nameof(profile))).SecretName);

    // ---- per-task assignment

    public (string? ProfileId, string? Model) GetTask(AiTask task)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT profile_id, model FROM task_models WHERE task = $t;";
        cmd.Parameters.AddWithValue("$t", task.ToString());
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : (null, null);
    }

    public void SetTask(AiTask task, string? profileId, string? model)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO task_models(task, profile_id, model) VALUES ($t, $p, $m)
            ON CONFLICT(task) DO UPDATE SET profile_id = excluded.profile_id, model = excluded.model;
            """;
        cmd.Parameters.AddWithValue("$t", task.ToString());
        cmd.Parameters.AddWithValue("$p", (object?)profileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", string.IsNullOrWhiteSpace(model) ? DBNull.Value : model);
        cmd.ExecuteNonQuery();
    }

    // ---- prices

    public IReadOnlyList<ModelPrice> ListPrices()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT model, input_per_mtok, output_per_mtok FROM model_prices ORDER BY model;";
        using var r = cmd.ExecuteReader();
        var list = new List<ModelPrice>();
        while (r.Read())
        {
            list.Add(new ModelPrice(r.GetString(0), (decimal)r.GetDouble(1), (decimal)r.GetDouble(2)));
        }

        return list;
    }

    public ModelPrice? GetPrice(string model) => ListPrices().FirstOrDefault(p => string.Equals(p.Model, model, StringComparison.OrdinalIgnoreCase));

    public void SetPrice(ModelPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO model_prices(model, input_per_mtok, output_per_mtok, updated_utc) VALUES ($m, $i, $o, $t)
            ON CONFLICT(model) DO UPDATE SET input_per_mtok = excluded.input_per_mtok, output_per_mtok = excluded.output_per_mtok, updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$m", price.Model.Trim());
        cmd.Parameters.AddWithValue("$i", (double)price.InputPerMillion);
        cmd.Parameters.AddWithValue("$o", (double)price.OutputPerMillion);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public void DeletePrice(string model)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM model_prices WHERE model = $m;";
        cmd.Parameters.AddWithValue("$m", model);
        cmd.ExecuteNonQuery();
    }
}

/// <summary>USD per million tokens, entered by the user.</summary>
public sealed record ModelPrice(string Model, decimal InputPerMillion, decimal OutputPerMillion)
{
    public decimal CostOf(LlmUsage usage) =>
        ((usage ?? LlmUsage.Zero).InputTokens * InputPerMillion / 1_000_000m) + (usage!.OutputTokens * OutputPerMillion / 1_000_000m);
}
