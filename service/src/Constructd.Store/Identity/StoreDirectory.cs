using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Construct.Keys;
using Constructd.Store.Records;
using Microsoft.Data.Sqlite;

namespace Constructd.Store.Identity;

/// <summary>A token the store issued, and whom it stands for.</summary>
/// <param name="PublicKey">The device's registered key; null for an engine participant.</param>
public sealed record TokenOwner(string Account, ParticipantKind Kind, string Id, ParticipantPublicKey? PublicKey);

/// <summary>A pairing as the store holds it (8.3).</summary>
public sealed record PairingState(
    string Id, ParticipantPublicKey PublicKey, string Name, DateTimeOffset ExpiresAt,
    string? Account, string? ApproverId, byte[]? Grant, byte[]? Signature, bool Redeemed);

/// <summary>The outcome of a recovery attempt (8.5).</summary>
public abstract record RecoveryOutcome
{
    public sealed record Recovered(RootSeal Wrap, string EnrollToken) : RecoveryOutcome;

    public sealed record Wrong : RecoveryOutcome;

    public sealed record RateLimited : RecoveryOutcome;
}

/// <summary>
/// The store's own registry (8.6: the store is trusted for membership and keeps the device
/// registry): devices with their public keys and status, the tokens it issued to devices and engine
/// participants, pairings, the per-user recovery verifier and wrap, the account's key wraps under
/// <c>U</c>, revocation acknowledgements and the setup code. All of it is in clear except what is
/// sealed or wrapped by participants; it lives next to the records in the same SQLite file.
///
/// One connection and one lock, like <see cref="RecordStore"/>: the operations are small and rare.
/// </summary>
public sealed class StoreDirectory : IDisposable
{
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EnrollTokenLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(15);
    public const int RecoveryAttempts = 5;

    private const string Device = "device";
    private const string Engine = "engine";
    private const string Enroll = "enroll";

    private readonly SqliteConnection _connection;
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    public StoreDirectory(string databasePath, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        if (databasePath != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Run("""
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS store_devices (
                account TEXT NOT NULL, id TEXT NOT NULL, name TEXT NOT NULL, public_key TEXT NOT NULL,
                status TEXT NOT NULL, created_at TEXT NOT NULL, revoked_at TEXT, PRIMARY KEY (account, id));
            CREATE TABLE IF NOT EXISTS store_tokens (
                hash BLOB PRIMARY KEY, account TEXT NOT NULL, kind TEXT NOT NULL, participant_id TEXT NOT NULL, expires_at TEXT);
            CREATE TABLE IF NOT EXISTS store_engines (
                account TEXT NOT NULL, id TEXT NOT NULL, host_key TEXT NOT NULL, created_at TEXT NOT NULL, PRIMARY KEY (account, id));
            CREATE TABLE IF NOT EXISTS store_acks (
                account TEXT NOT NULL, device_id TEXT NOT NULL, engine_id TEXT NOT NULL, at TEXT NOT NULL,
                PRIMARY KEY (account, device_id, engine_id));
            CREATE TABLE IF NOT EXISTS store_pairings (
                id TEXT PRIMARY KEY, public_key TEXT NOT NULL, name TEXT NOT NULL, expires_at TEXT NOT NULL,
                account TEXT, approver_id TEXT, grant_bytes BLOB, signature BLOB, redeemed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS store_recovery (
                account TEXT PRIMARY KEY, verifier BLOB NOT NULL, wrap BLOB NOT NULL, epoch INTEGER NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS store_keys (account TEXT PRIMARY KEY, body TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS store_setup (one INTEGER PRIMARY KEY CHECK (one = 1), code_hash BLOB NOT NULL);
            """);
    }

    public void Dispose() => _connection.Dispose();

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---- tokens -----------------------------------------------------------------------------------

    /// <summary>Whom a device or engine token stands for; null for an unknown, deleted or enrolment token.</summary>
    public TokenOwner? Resolve(string token)
    {
        lock (_lock)
        {
            using var command = Command("""
                SELECT t.account, t.kind, t.participant_id, d.public_key, d.status FROM store_tokens t
                LEFT JOIN store_devices d ON t.kind = 'device' AND d.account = t.account AND d.id = t.participant_id
                WHERE t.hash = $h AND t.kind IN ('device', 'engine')
                """, ("$h", Tokens.Hash(token)));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var kind = reader.GetString(1) == Device ? ParticipantKind.Device : ParticipantKind.Engine;
            if (kind == ParticipantKind.Device && (reader.IsDBNull(3) || reader.GetString(4) != "active")) return null;
            return new TokenOwner(reader.GetString(0), kind, reader.GetString(2), kind == ParticipantKind.Device ? ParticipantPublicKey.Parse(reader.GetString(3)) : null);
        }
    }

    // ---- devices ----------------------------------------------------------------------------------

    /// <summary>
    /// The first device of an account (5.4): registered only while the account has no device. With
    /// <paramref name="setupCode"/> (a stand-alone store) only while the whole store has no device,
    /// and the code is used up.
    /// </summary>
    public string? EnrollFirst(string account, string name, ParticipantPublicKey key, string? setupCode)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            if (setupCode is not null)
            {
                if (Scalar(tx, "SELECT COUNT(*) FROM store_devices") > 0) return null;
                using var check = Command("SELECT code_hash FROM store_setup WHERE one = 1");
                check.Transaction = tx;
                if (check.ExecuteScalar() is not byte[] hash || !CryptographicOperations.FixedTimeEquals(hash, Tokens.Hash(setupCode))) return null;
                Run(tx, "DELETE FROM store_setup");
            }
            else if (Scalar(tx, "SELECT COUNT(*) FROM store_devices WHERE account = $a", ("$a", account)) > 0)
            {
                return null;
            }

            var token = Register(tx, account, name, key);
            tx.Commit();
            return token;
        }
    }

    /// <summary>A device enrolled with the one-time token a recovery handed out (8.5); returns account and token.</summary>
    public (string Account, string Token)? EnrollRecovered(string enrollToken, string name, ParticipantPublicKey key)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var find = Command("SELECT account, expires_at FROM store_tokens WHERE hash = $h AND kind = 'enroll'", ("$h", Tokens.Hash(enrollToken)));
            find.Transaction = tx;
            string account;
            using (var reader = find.ExecuteReader())
            {
                if (!reader.Read() || Parse(reader.GetString(1)) < Now) return null;
                account = reader.GetString(0);
            }

            Run(tx, "DELETE FROM store_tokens WHERE hash = $h", ("$h", Tokens.Hash(enrollToken)));
            if (Exists(tx, account, key.Id)) return null;
            var token = Register(tx, account, name, key);
            tx.Commit();
            return (account, token);
        }
    }

    public IReadOnlyList<DeviceEntry> Devices(string account)
    {
        lock (_lock)
        {
            var acks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (var command = Command("SELECT device_id, engine_id FROM store_acks WHERE account = $a ORDER BY engine_id", ("$a", account)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!acks.TryGetValue(reader.GetString(0), out var list)) acks[reader.GetString(0)] = list = [];
                    list.Add(reader.GetString(1));
                }
            }

            var result = new List<DeviceEntry>();
            using (var command = Command("SELECT id, name, public_key, status, created_at, revoked_at FROM store_devices WHERE account = $a ORDER BY created_at, id", ("$a", account)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    result.Add(new DeviceEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        Parse(reader.GetString(4)), reader.IsDBNull(5) ? null : Parse(reader.GetString(5)),
                        acks.TryGetValue(reader.GetString(0), out var list) ? list : []));
                }
            }

            return result;
        }
    }

    /// <summary>Revocation (8.4, first release): the device's store tokens go at once and its entry is marked. False when unknown.</summary>
    public bool Revoke(string account, string deviceId)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            if (!Exists(tx, account, deviceId)) return false;
            Run(tx, "UPDATE store_devices SET status = 'revoked', revoked_at = COALESCE(revoked_at, $now) WHERE account = $a AND id = $id",
                ("$a", account), ("$id", deviceId), ("$now", Stamp(Now)));
            Run(tx, "DELETE FROM store_tokens WHERE account = $a AND kind = 'device' AND participant_id = $id", ("$a", account), ("$id", deviceId));
            tx.Commit();
            return true;
        }
    }

    /// <summary>An engine confirms it no longer serves a revoked device. False when the device is not revoked.</summary>
    public bool Acknowledge(string account, string deviceId, string engineId)
    {
        lock (_lock)
        {
            using var check = Command("SELECT status FROM store_devices WHERE account = $a AND id = $id", ("$a", account), ("$id", deviceId));
            if (check.ExecuteScalar() as string != "revoked") return false;
            using var tx = _connection.BeginTransaction();
            Run(tx, "INSERT OR IGNORE INTO store_acks (account, device_id, engine_id, at) VALUES ($a, $d, $e, $now)",
                ("$a", account), ("$d", deviceId), ("$e", engineId), ("$now", Stamp(Now)));
            tx.Commit();
            return true;
        }
    }

    // ---- engines ----------------------------------------------------------------------------------

    /// <summary>Registers an engine as a participant of the account (8.7) and issues its token; an earlier token of that engine stops working.</summary>
    public string RegisterEngine(string account, string engineId, ParticipantPublicKey hostKey)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            Run(tx, "INSERT INTO store_engines (account, id, host_key, created_at) VALUES ($a, $id, $k, $now) ON CONFLICT (account, id) DO UPDATE SET host_key = $k",
                ("$a", account), ("$id", engineId), ("$k", hostKey.ToString()), ("$now", Stamp(Now)));
            Run(tx, "DELETE FROM store_tokens WHERE account = $a AND kind = 'engine' AND participant_id = $id", ("$a", account), ("$id", engineId));
            var token = Tokens.New();
            Run(tx, "INSERT INTO store_tokens (hash, account, kind, participant_id) VALUES ($h, $a, 'engine', $id)",
                ("$h", Tokens.Hash(token)), ("$a", account), ("$id", engineId));
            tx.Commit();
            return token;
        }
    }

    // ---- pairing ----------------------------------------------------------------------------------

    public (string Id, DateTimeOffset ExpiresAt) CreatePairing(ParticipantPublicKey key, string name)
    {
        lock (_lock)
        {
            var id = Tokens.New()[..22];
            var expires = Now + PairingLifetime;
            using var tx = _connection.BeginTransaction();
            Run(tx, "DELETE FROM store_pairings WHERE expires_at < $old", ("$old", Stamp(Now - TimeSpan.FromDays(1))));
            Run(tx, "INSERT INTO store_pairings (id, public_key, name, expires_at) VALUES ($id, $k, $n, $e)",
                ("$id", id), ("$k", key.ToString()), ("$n", name), ("$e", Stamp(expires)));
            tx.Commit();
            return (id, expires);
        }
    }

    /// <summary>The pairing, or null when it does not exist or has expired.</summary>
    public PairingState? Pairing(string id)
    {
        lock (_lock)
        {
            using var command = Command("SELECT id, public_key, name, expires_at, account, approver_id, grant_bytes, signature, redeemed FROM store_pairings WHERE id = $id", ("$id", id));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var state = new PairingState(reader.GetString(0), ParticipantPublicKey.Parse(reader.GetString(1)), reader.GetString(2), Parse(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : (byte[])reader.GetValue(6), reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7), reader.GetInt64(8) != 0);
            return state.ExpiresAt < Now ? null : state;
        }
    }

    /// <summary>Stores the approver's grant; false when the pairing is gone, expired or already approved.</summary>
    public bool Approve(string id, string account, string approverId, byte[] grant, byte[] signature)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            using var command = Command("""
                UPDATE store_pairings SET account = $a, approver_id = $ap, grant_bytes = $g, signature = $s
                WHERE id = $id AND account IS NULL AND expires_at >= $now
                """, ("$a", account), ("$ap", approverId), ("$g", grant), ("$s", signature), ("$id", id), ("$now", Stamp(Now)));
            command.Transaction = tx;
            if (command.ExecuteNonQuery() != 1) return false;
            tx.Commit();
            return true;
        }
    }

    /// <summary>
    /// The one redemption of an approved pairing: marks it redeemed, registers the device in the
    /// approver's account and issues its store token. Null when the pairing is gone, expired, not
    /// approved, already redeemed, its approver has been revoked since, or the device is already registered.
    /// </summary>
    public string? Redeem(string id)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            // The approver must still be active: revoking a device also voids the grants it approved.
            using var find = Command("""
                SELECT p.public_key, p.name, p.account FROM store_pairings p
                JOIN store_devices d ON d.account = p.account AND d.id = p.approver_id AND d.status = 'active'
                WHERE p.id = $id AND p.redeemed = 0 AND p.expires_at >= $now
                """, ("$id", id), ("$now", Stamp(Now)));
            find.Transaction = tx;
            string key, name, account;
            using (var reader = find.ExecuteReader())
            {
                if (!reader.Read()) return null;
                (key, name, account) = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
            }

            Run(tx, "UPDATE store_pairings SET redeemed = 1 WHERE id = $id", ("$id", id));
            var publicKey = ParticipantPublicKey.Parse(key);
            if (Exists(tx, account, publicKey.Id)) return null;
            var token = Register(tx, account, name, publicKey);
            tx.Commit();
            return token;
        }
    }

    // ---- recovery ---------------------------------------------------------------------------------

    /// <summary>Sets or replaces the account's recovery verifier and wrap in one write (8.5).</summary>
    public void SetRecovery(string account, byte[] verifier, RootSeal wrap)
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            Run(tx, """
                INSERT INTO store_recovery (account, verifier, wrap, epoch, updated_at) VALUES ($a, $v, $w, $e, $now)
                ON CONFLICT (account) DO UPDATE SET verifier = $v, wrap = $w, epoch = $e, updated_at = $now
                """, ("$a", account), ("$v", verifier), ("$w", wrap.Sealed), ("$e", wrap.Epoch), ("$now", Stamp(Now)));
            tx.Commit();
        }
    }

    /// <summary>
    /// Checks a recovery secret against the account's verifier. At most
    /// <see cref="RecoveryAttempts"/> failures per account in <see cref="RecoveryWindow"/>; after
    /// that every attempt is refused until the window has passed. The counter lives in memory.
    /// </summary>
    public RecoveryOutcome Recover(string account, byte[] secret)
    {
        lock (_lock)
        {
            var now = Now;
            if (!_failures.TryGetValue(account, out var failures)) _failures[account] = failures = [];
            failures.RemoveAll(at => at < now - RecoveryWindow);
            if (failures.Count >= RecoveryAttempts) return new RecoveryOutcome.RateLimited();

            byte[]? verifier = null, wrap = null;
            long epoch = 0;
            using (var command = Command("SELECT verifier, wrap, epoch FROM store_recovery WHERE account = $a", ("$a", account)))
            using (var reader = command.ExecuteReader())
            {
                if (reader.Read()) (verifier, wrap, epoch) = ((byte[])reader.GetValue(0), (byte[])reader.GetValue(1), reader.GetInt64(2));
            }

            if (verifier is null || !CryptographicOperations.FixedTimeEquals(verifier, RecoveryCode.Verifier(secret)))
            {
                failures.Add(now);
                return new RecoveryOutcome.Wrong();
            }

            var token = Tokens.New();
            using var tx = _connection.BeginTransaction();
            Run(tx, "DELETE FROM store_tokens WHERE kind = 'enroll' AND expires_at < $now", ("$now", Stamp(now)));
            Run(tx, "INSERT INTO store_tokens (hash, account, kind, participant_id, expires_at) VALUES ($h, $a, 'enroll', '', $e)",
                ("$h", Tokens.Hash(token)), ("$a", account), ("$e", Stamp(now + EnrollTokenLifetime)));
            tx.Commit();
            return new RecoveryOutcome.Recovered(new RootSeal(epoch, wrap!), token);
        }
    }

    // ---- key wraps --------------------------------------------------------------------------------

    public WrappedKeys? Keys(string account)
    {
        lock (_lock)
        {
            using var command = Command("SELECT body FROM store_keys WHERE account = $a", ("$a", account));
            return command.ExecuteScalar() is string body ? JsonSerializer.Deserialize<WrappedKeys>(body, StoreJson.Options) : null;
        }
    }

    /// <summary>Stores the account's key wraps once; false when they exist (replacing them is rotation, 8.4).</summary>
    public bool PutKeys(string account, WrappedKeys keys)
    {
        lock (_lock)
        {
            using var command = Command("INSERT OR IGNORE INTO store_keys (account, body) VALUES ($a, $b)",
                ("$a", account), ("$b", JsonSerializer.Serialize(keys, StoreJson.Options)));
            return command.ExecuteNonQuery() == 1;
        }
    }

    // ---- setup code -------------------------------------------------------------------------------

    /// <summary>
    /// For a stand-alone store without devices: a new setup code (only its hash is kept), replacing
    /// any earlier one. Null once the store has a device.
    /// </summary>
    public string? NewSetupCode()
    {
        lock (_lock)
        {
            using var tx = _connection.BeginTransaction();
            if (Scalar(tx, "SELECT COUNT(*) FROM store_devices") > 0) return null;
            var code = Tokens.New();
            Run(tx, "INSERT INTO store_setup (one, code_hash) VALUES (1, $h) ON CONFLICT (one) DO UPDATE SET code_hash = $h", ("$h", Tokens.Hash(code)));
            tx.Commit();
            return code;
        }
    }

    // ---- internals --------------------------------------------------------------------------------

    private string Register(SqliteTransaction tx, string account, string name, ParticipantPublicKey key)
    {
        Run(tx, "INSERT INTO store_devices (account, id, name, public_key, status, created_at) VALUES ($a, $id, $n, $k, 'active', $now)",
            ("$a", account), ("$id", key.Id), ("$n", name), ("$k", key.ToString()), ("$now", Stamp(Now)));
        var token = Tokens.New();
        Run(tx, "INSERT INTO store_tokens (hash, account, kind, participant_id) VALUES ($h, $a, 'device', $id)",
            ("$h", Tokens.Hash(token)), ("$a", account), ("$id", key.Id));
        return token;
    }

    private bool Exists(SqliteTransaction tx, string account, string id) =>
        Scalar(tx, "SELECT COUNT(*) FROM store_devices WHERE account = $a AND id = $id", ("$a", account), ("$id", id)) > 0;

    private long Scalar(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = tx;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Run(SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = tx;
        command.ExecuteNonQuery();
    }

    private void Run(string sql)
    {
        using var command = Command(sql);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string at) => DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
}

/// <summary>A device in the store's registry, with the engines that acknowledged its revocation.</summary>
public sealed record DeviceEntry(
    string Id, string Name, string PublicKey, string Status, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, IReadOnlyList<string> AcknowledgedBy);
