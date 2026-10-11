using SSGen.Core.Data;
using SSGen.Core.Security;
using SSGen.Core.Settings;

namespace SSGen.Core.Tests;

public class AppPathsTests
{
    [Fact]
    public void Legacy_data_folder_is_moved_once()
    {
        using var dir = new TempDir();
        var legacy = dir.Combine("StoreShotStudio");
        var target = dir.Combine("SSGen");
        dir.Touch("StoreShotStudio", "app.db");

        AppPaths.MigrateLegacyFolder(legacy, target);
        Assert.True(File.Exists(Path.Combine(target, "app.db")));
        Assert.False(Directory.Exists(legacy));

        // An existing target is never overwritten.
        dir.Touch("StoreShotStudio", "other.db");
        AppPaths.MigrateLegacyFolder(legacy, target);
        Assert.True(File.Exists(Path.Combine(legacy, "other.db")));
        Assert.False(File.Exists(Path.Combine(target, "other.db")));
    }
}

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
    public void Upgrading_an_existing_database_keeps_a_backup_first()
    {
        using var dir = new TempDir();
        var path = dir.Combine("project.db");
        var db = new SqliteDatabase(path, MigrationSet.Project);
        db.Migrate();
        Assert.Empty(db.Backups()); // a new database has nothing to back up

        // Pretend it's an older schema with data in it, then upgrade.
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO project_meta(key, value) VALUES ('marker', 'kept'); DROP TABLE captions; ALTER TABLE renders DROP COLUMN locale; PRAGMA user_version = 6;";
            cmd.ExecuteNonQuery();
        }

        db.Migrate();
        var backup = Assert.Single(db.Backups());
        Assert.EndsWith("project.db.v6.bak", backup, StringComparison.Ordinal);
        using var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={backup};Pooling=False");
        copy.Open();
        using var read = copy.CreateCommand();
        read.CommandText = "SELECT value FROM project_meta WHERE key = 'marker';";
        Assert.Equal("kept", read.ExecuteScalar());
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
    public void Reads_secrets_saved_before_the_rename()
    {
        using var dir = new TempDir();
        var db = TestDb.CreateApp(dir);
        var legacy = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes("old-key"), System.Text.Encoding.UTF8.GetBytes("StoreShotStudio.Secrets.v1"),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        using (var c = db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO secrets(name, ciphertext, updated_utc) VALUES ('old', $c, 'now');";
            cmd.Parameters.AddWithValue("$c", legacy);
            cmd.ExecuteNonQuery();
        }

        Assert.Equal("old-key", new DpapiSecretStore(db).Load("old"));
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
