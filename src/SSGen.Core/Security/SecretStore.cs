using System.Security.Cryptography;
using System.Text;
using SSGen.Core.Data;

namespace SSGen.Core.Security;

/// <summary>Stores secrets such as API keys. Implementations must never persist plaintext.</summary>
public interface ISecretStore
{
    void Save(string name, string value);

    string? Load(string name);

    bool Delete(string name);

    IReadOnlyList<string> ListNames();
}

/// <summary>
/// Encrypts secrets with Windows DPAPI (CurrentUser scope) and stores the ciphertext in the app database.
/// Only the same Windows user on the same machine can decrypt them.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore
{
    // Additional entropy so other DPAPI consumers running as this user can't trivially reuse our blobs.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SSGen.Secrets.v1");

    // Entropy used before the rename to SSGen; secrets saved with it are still readable.
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("StoreShotStudio.Secrets.v1");

    private readonly SqliteDatabase _db;

    public DpapiSecretStore(SqliteDatabase appDatabase) => _db = appDatabase;

    public void Save(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO secrets(name, ciphertext, updated_utc) VALUES ($n, $c, $t)
            ON CONFLICT(name) DO UPDATE SET ciphertext = excluded.ciphertext, updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$c", cipher);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public string? Load(string name)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT ciphertext FROM secrets WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        if (cmd.ExecuteScalar() is not byte[] cipher)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Unprotect(cipher));
        }
        catch (CryptographicException ex)
        {
            throw new UserFacingException(
                $"The saved secret \"{name}\" can't be decrypted. It was probably saved by another Windows user or on another PC.",
                "Re-enter the key in Settings.",
                ex);
        }
    }

    public bool Delete(string name)
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM secrets WHERE name = $n;";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<string> ListNames()
    {
        using var connection = _db.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM secrets ORDER BY name;";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        try
        {
            return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return ProtectedData.Unprotect(cipher, LegacyEntropy, DataProtectionScope.CurrentUser);
        }
    }
}