using System.Security.Cryptography;
using Construct.Keys;
using Constructd.Sync.Sealing;
using Microsoft.Data.Sqlite;

namespace Constructd.Sync.Participation;

/// <summary>
/// What an engine keeps as a participant (8.1, 8.7), in its own SQLite file: its host key pair, the
/// keys devices granted it (sealed to the host key, so they are wrapped under it at rest) and the
/// device tokens it issued, bound to the device keys of the <c>device</c> records.
///
/// The host key's private half is in the same file, readable by the service account; the host
/// administrator can read it, as they can read the running service's memory (S2).
/// </summary>
public sealed class EngineKeyStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _lock = new();

    /// <param name="databasePath">The SQLite file; <c>:memory:</c> for an engine that forgets.</param>
    public EngineKeyStore(string databasePath, string engineId)
    {
        EngineId = engineId;
        if (databasePath != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _connection.Open();
        Run("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS engine_host_key (one INTEGER PRIMARY KEY CHECK (one = 1), private_key TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS engine_grants (account TEXT NOT NULL, name TEXT NOT NULL, epoch INTEGER NOT NULL, sealed BLOB NOT NULL,
                PRIMARY KEY (account, name, epoch));
            CREATE TABLE IF NOT EXISTS engine_device_tokens (hash BLOB PRIMARY KEY, account TEXT NOT NULL, device_id TEXT NOT NULL);
            """);

        using var read = Command("SELECT private_key FROM engine_host_key WHERE one = 1");
        if (read.ExecuteScalar() is string existing)
        {
            HostKey = ParticipantKey.Import(existing);
        }
        else
        {
            HostKey = ParticipantKey.Generate();
            Run("INSERT INTO engine_host_key (one, private_key) VALUES (1, $k)", ("$k", HostKey.Export()));
        }
    }

    public string EngineId { get; }

    public ParticipantKey HostKey { get; }

    // ---- grants -----------------------------------------------------------------------------------

    /// <summary>
    /// Keeps a granted key after checking that it opens under this engine's host key. Only
    /// <c>D</c> and <c>K</c> are accepted; an engine never holds <c>U</c> or <c>B</c>'s private half.
    /// </summary>
    public void PutGrant(string account, EngineGrant grant)
    {
        if (!EngineGrants.IsGrantable(grant.Name)) throw new ArgumentException($"An engine is never granted '{grant.Name}'.", nameof(grant));
        var key = EngineGrants.Open(HostKey, EngineId, account, grant);
        if (key.Length != 32) throw new CryptographicException("A granted key is 256 bits.");
        CryptographicOperations.ZeroMemory(key);
        lock (_lock)
        {
            Run("INSERT INTO engine_grants (account, name, epoch, sealed) VALUES ($a, $n, $e, $s) ON CONFLICT (account, name, epoch) DO UPDATE SET sealed = $s",
                ("$a", account), ("$n", grant.Name), ("$e", grant.Epoch), ("$s", grant.Sealed));
        }
    }

    /// <summary>The granted key, unsealed; null when no device granted it.</summary>
    public byte[]? GrantedKey(string account, string name, long epoch)
    {
        byte[]? sealedBytes;
        lock (_lock)
        {
            using var command = Command("SELECT sealed FROM engine_grants WHERE account = $a AND name = $n AND epoch = $e", ("$a", account), ("$n", name), ("$e", epoch));
            sealedBytes = command.ExecuteScalar() as byte[];
        }

        return sealedBytes is null ? null : EngineGrants.Open(HostKey, EngineId, account, new EngineGrant(name, epoch, sealedBytes));
    }

    // ---- device tokens ----------------------------------------------------------------------------

    public string IssueDeviceToken(string account, string deviceId)
    {
        var token = Tokens.New();
        lock (_lock)
        {
            Run("INSERT INTO engine_device_tokens (hash, account, device_id) VALUES ($h, $a, $d)", ("$h", Tokens.Hash(token)), ("$a", account), ("$d", deviceId));
        }

        return token;
    }

    public (string Account, string DeviceId)? TokenOwner(string token)
    {
        lock (_lock)
        {
            using var command = Command("SELECT account, device_id FROM engine_device_tokens WHERE hash = $h", ("$h", Tokens.Hash(token)));
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
        }
    }

    public void DeleteDeviceTokens(string account, string deviceId)
    {
        lock (_lock)
        {
            Run("DELETE FROM engine_device_tokens WHERE account = $a AND device_id = $d", ("$a", account), ("$d", deviceId));
        }
    }

    public void Dispose()
    {
        HostKey.Dispose();
        _connection.Dispose();
    }

    private void Run(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}

/// <summary>
/// The engine's <c>D</c> (8.7): read from the grant a device made to this engine, in place of the
/// key file of <see cref="FileRecordKeyProvider"/>.
/// </summary>
public sealed class GrantedRecordKeyProvider(EngineKeyStore keys) : IRecordKeyProvider
{
    public Task<byte[]> GetKeyAsync(string account, long epoch, CancellationToken ct) =>
        Task.FromResult(keys.GrantedKey(account, EngineGrants.Records, epoch)
            ?? throw new CryptographicException($"No device has granted this engine the record key of epoch {epoch}."));
}
