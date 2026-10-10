using System.Text.Json;
using System.Text.Json.Serialization;
using SSGen.Core.Data;

namespace SSGen.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    void Save(AppSettings settings);
}

public sealed class SettingsService : ISettingsService
{
    private const string Key = "app";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteDatabase _db;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsService(SqliteDatabase appDatabase)
    {
        _db = appDatabase;
        _current = Load();
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using (var connection = _db.Open())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            cmd.Parameters.AddWithValue("$k", Key);
            cmd.Parameters.AddWithValue("$v", JsonSerializer.Serialize(settings, JsonOptions));
            cmd.ExecuteNonQuery();
        }

        lock (_gate)
        {
            _current = settings;
        }

        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", Key);
        if (cmd.ExecuteScalar() is not string json)
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Corrupt settings should never brick the app; fall back to defaults.
            return new AppSettings();
        }
    }
}
