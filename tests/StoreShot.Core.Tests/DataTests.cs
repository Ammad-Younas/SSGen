using StoreShot.Core.Data;
using StoreShot.Core.Security;
using StoreShot.Core.Settings;

namespace StoreShot.Core.Tests;

public class MigrationRunnerTests
{
    [Theory]
    [InlineData(MigrationSet.App)]
    [InlineData(MigrationSet.Project)]
    public void Migrate_applies_all_scripts_and_is_idempotent(MigrationSet set)
    {
        using var dir = new TempDir();
        var db = new SqliteDatabase(dir.Combine("x.db"), set);
        var expected = MigrationRunner.Load(set).Count;

        Assert.True(expected >= 1);
        Assert.Equal(expected, db.Migrate());
        Assert.Equal(expected, db.Migrate());
    }

    [Fact]
    public void Migrate_refuses_database_from_newer_version()
    {
        using var dir = new TempDir();
        var db = new SqliteDatabase(dir.Combine("x.db"), MigrationSet.App);
        db.Migrate();
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 999;";
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<UserFacingException>(() => db.Migrate());
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void Defaults_when_nothing_saved()
    {
        using var dir = new TempDir();
        var s = new SettingsService(TestDb.CreateApp(dir)).Current;

        Assert.Equal(AppTheme.System, s.Theme);
        Assert.True(s.SafeMode);
        Assert.False(s.SetupCompleted);
        Assert.Null(s.AndroidSdkPath);
    }

    [Fact]
    public void Save_round_trips_and_raises_Changed()
    {
        using var dir = new TempDir();
        var db = TestDb.CreateApp(dir);
        var service = new SettingsService(db);
        AppSettings? raised = null;
        service.Changed += (_, s) => raised = s;

        var updated = service.Current with { Theme = AppTheme.Dark, AndroidSdkPath = @"D:\sdk", SetupCompleted = true };
        service.Save(updated);

        Assert.Equal(updated, raised);
        Assert.Equal(updated, new SettingsService(db).Current);
    }

    [Fact]
    public void Corrupt_or_unknown_json_does_not_break_loading()
    {
        using var dir = new TempDir();
        var db = TestDb.CreateApp(dir);
        void Write(string json)
        {
            using var c = db.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES ('app', $v);";
            cmd.Parameters.AddWithValue("$v", json);
            cmd.ExecuteNonQuery();
        }

        Write("{not json");
        Assert.Equal(new AppSettings(), new SettingsService(db).Current);

        Write("""{"Theme":"Light","SomeFutureField":42}""");
        Assert.Equal(AppTheme.Light, new SettingsService(db).Current.Theme);
    }
}

public class DpapiSecretStoreTests
{
    [Fact]
    public void Round_trips_and_never_stores_plaintext()
    {
        using var dir = new TempDir();
        var db = TestDb.CreateApp(dir);
        var store = new DpapiSecretStore(db);
        const string key = "sk-test-SUPER-SECRET-123";

        store.Save("provider:anthropic", key);

        Assert.Equal(key, store.Load("provider:anthropic"));
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT ciphertext FROM secrets";
            var cipher = (byte[])cmd.ExecuteScalar()!;
            Assert.DoesNotContain("SUPER-SECRET", System.Text.Encoding.UTF8.GetString(cipher), StringComparison.Ordinal);
        }

        // Scan the raw files too (with WAL, recent writes live in the -wal file).
        foreach (var file in new[] { db.Path, db.Path + "-wal" }.Where(File.Exists))
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, System.Text.Encoding.Latin1);
            Assert.DoesNotContain("SUPER-SECRET", reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Overwrite_list_and_delete()
    {
        using var dir = new TempDir();
        var store = new DpapiSecretStore(TestDb.CreateApp(dir));

        store.Save("b", "1");
        store.Save("a", "2");
        store.Save("b", "3");

        Assert.Equal(["a", "b"], store.ListNames());
        Assert.Equal("3", store.Load("b"));
        Assert.True(store.Delete("b"));
        Assert.False(store.Delete("b"));
        Assert.Null(store.Load("b"));
    }

    [Fact]
    public void Undecryptable_blob_gives_user_facing_error()
    {
        using var dir = new TempDir();
        var db = TestDb.CreateApp(dir);
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO secrets(name, ciphertext, updated_utc) VALUES ('x', $c, 'now');";
            cmd.Parameters.AddWithValue("$c", new byte[] { 1, 2, 3, 4 });
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<UserFacingException>(() => new DpapiSecretStore(db).Load("x"));
        Assert.NotNull(ex.SuggestedFix);
    }
}
