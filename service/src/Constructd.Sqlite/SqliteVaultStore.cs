using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Data.Sqlite;
namespace Constructd.Sqlite;

/// <summary>
/// The hosted key vault's tables (M850). Entries and devices are columns; leases, cleanups, scrub jobs
/// and file decisions keep their record as JSON next to the owner/VM columns they are found by.
/// </summary>
public sealed class SqliteVaultStore(SqliteDatabase database) : IVaultStore
{
    private static string Table<T>() => typeof(T) == typeof(VaultLease) ? "vault_leases"
        : typeof(T) == typeof(VaultCleanup) ? "vault_cleanups"
        : typeof(T) == typeof(VaultScrubJob) ? "vault_scrubs"
        : typeof(T) == typeof(VaultFileDecision) ? "vault_files"
        : throw new ArgumentException($"{typeof(T).Name} is not a vault record.");

    private static VaultOwnerState ReadOwner(SqliteDataReader r) => new(r.GetString("owner"), r.GetLong("revision"),
        r.GetStringOrNull("mode"), r.GetStringOrNull("key_check"), r.GetStringOrNull("wrapped_key"));

    public Task<VaultOwnerState?> GetOwnerAsync(string owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vault_owners WHERE owner=@owner"; cmd.With("@owner", owner);
        using var r = cmd.ExecuteReader(); return Task.FromResult(r.Read() ? ReadOwner(r) : null);
    }

    public Task<IReadOnlyList<VaultOwnerState>> ListOwnersAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vault_owners ORDER BY owner";
        using var r = cmd.ExecuteReader(); var owners = new List<VaultOwnerState>(); while (r.Read()) owners.Add(ReadOwner(r));
        return Task.FromResult<IReadOnlyList<VaultOwnerState>>(owners);
    }

    public Task SaveSettingsAsync(string owner, string mode, string keyCheck, string? wrappedKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vault_owners (owner,revision,mode,key_check,wrapped_key) VALUES (@owner,0,@mode,@check,@wrapped)
            ON CONFLICT(owner) DO UPDATE SET mode=@mode,key_check=@check,wrapped_key=@wrapped
            """;
        cmd.With("@owner", owner).With("@mode", mode).With("@check", keyCheck).With("@wrapped", wrappedKey).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VaultEntry>> ListEntriesAsync(string owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vault_entries WHERE owner=@owner ORDER BY name"; cmd.With("@owner", owner);
        using var r = cmd.ExecuteReader(); var entries = new List<VaultEntry>();
        while (r.Read())
            entries.Add(new(r.GetString("name"), r.GetString("description"), r.GetBool("has_username"), r.GetStringOrNull("payload"),
                r.GetLong("updated_at"), r.GetString("updated_by"), r.GetBool("deleted")));
        return Task.FromResult<IReadOnlyList<VaultEntry>>(entries);
    }

    public Task<long> SaveEntriesAsync(string owner, IReadOnlyList<VaultEntry> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var tx = c.BeginTransaction();
        foreach (var e in entries)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO vault_entries (owner,name,description,has_username,payload,updated_at,updated_by,deleted)
                VALUES (@owner,@name,@description,@user,@payload,@at,@by,@deleted)
                ON CONFLICT(owner,name) DO UPDATE SET description=@description,has_username=@user,payload=@payload,
                updated_at=@at,updated_by=@by,deleted=@deleted
                """;
            cmd.With("@owner", owner).With("@name", e.Name).With("@description", e.Description).With("@user", e.HasUsername ? 1 : 0)
                .With("@payload", e.Payload).With("@at", e.UpdatedAt).With("@by", e.UpdatedBy).With("@deleted", e.Deleted ? 1 : 0).ExecuteNonQuery();
        }
        using var bump = c.CreateCommand(); bump.Transaction = tx;
        bump.CommandText = """
            INSERT INTO vault_owners (owner,revision) VALUES (@owner,1)
            ON CONFLICT(owner) DO UPDATE SET revision=revision+1 RETURNING revision
            """;
        var revision = Convert.ToInt64(bump.With("@owner", owner).ExecuteScalar());
        tx.Commit();
        return Task.FromResult(revision);
    }

    public Task<int> PruneTombstonesAsync(long beforeMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vault_entries WHERE deleted=1 AND updated_at<@before"; cmd.With("@before", beforeMs);
        return Task.FromResult(cmd.ExecuteNonQuery());
    }

    public Task<IReadOnlyList<T>> ListAsync<T>(string? owner, CancellationToken ct) where T : class, IVaultRecord
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT body FROM {Table<T>()}" + (owner is null ? "" : " WHERE owner=@owner") + " ORDER BY rowid";
        if (owner is not null) cmd.With("@owner", owner);
        using var r = cmd.ExecuteReader(); var records = new List<T>();
        while (r.Read()) if (WireJson.Read<T>(r.GetString(0)) is { } record) records.Add(record);
        return Task.FromResult<IReadOnlyList<T>>(records);
    }

    public Task SaveAsync<T>(T record, CancellationToken ct) where T : class, IVaultRecord
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO {Table<T>()} (id,owner,vm,body) VALUES (@id,@owner,@vm,@body) ON CONFLICT(id) DO UPDATE SET owner=@owner,vm=@vm,body=@body";
        cmd.With("@id", record.Id).With("@owner", record.Owner).With("@vm", record.Vm).With("@body", WireJson.Serialize(record)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string id, CancellationToken ct) where T : class, IVaultRecord
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table<T>()} WHERE id=@id"; cmd.With("@id", id).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private static VaultDevice ReadDevice(SqliteDataReader r) => new(r.GetString("id"), r.GetString("owner"), r.GetString("label"),
        r.GetString("token_hash"), SqliteDatabase.ReadTime(r.GetString("created_at")), SqliteDatabase.ReadTimeOrNull(r["last_used_at"]));

    public Task<IReadOnlyList<VaultDevice>> ListDevicesAsync(string owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vault_devices WHERE owner=@owner ORDER BY created_at, id"; cmd.With("@owner", owner);
        using var r = cmd.ExecuteReader(); var devices = new List<VaultDevice>(); while (r.Read()) devices.Add(ReadDevice(r));
        return Task.FromResult<IReadOnlyList<VaultDevice>>(devices);
    }

    public Task AddDeviceAsync(VaultDevice device, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO vault_devices (id,owner,label,token_hash,created_at,last_used_at) VALUES (@id,@owner,@label,@hash,@created,@used)";
        cmd.With("@id", device.Id).With("@owner", device.Owner).With("@label", device.Label).With("@hash", device.TokenHash)
            .With("@created", SqliteDatabase.Text(device.CreatedAt)).With("@used", SqliteDatabase.TextOrNull(device.LastUsedAt)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteDeviceAsync(string owner, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM vault_devices WHERE id=@id AND owner=@owner";
        return Task.FromResult(cmd.With("@id", id).With("@owner", owner).ExecuteNonQuery() == 1);
    }

    public Task<VaultDevice?> FindDeviceAsync(string tokenHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vault_devices WHERE token_hash=@hash"; cmd.With("@hash", tokenHash);
        using var r = cmd.ExecuteReader(); return Task.FromResult(r.Read() ? ReadDevice(r) : null);
    }

    public Task TouchDeviceAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE vault_devices SET last_used_at=@at WHERE id=@id";
        cmd.With("@id", id).With("@at", SqliteDatabase.Text(at)).ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task AppendEventAsync(string owner, VaultEvent entry, int keep, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var tx = c.BeginTransaction();
        using (var insert = c.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO vault_events (owner,at,vm,text,warning) VALUES (@owner,@at,@vm,@text,@warning)";
            insert.With("@owner", owner).With("@at", SqliteDatabase.Text(entry.At)).With("@vm", entry.Vm).With("@text", entry.Text)
                .With("@warning", entry.Warning ? 1 : 0).ExecuteNonQuery();
        }
        using (var trim = c.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = "DELETE FROM vault_events WHERE owner=@owner AND id NOT IN (SELECT id FROM vault_events WHERE owner=@owner ORDER BY id DESC LIMIT @keep)";
            trim.With("@owner", owner).With("@keep", keep).ExecuteNonQuery();
        }
        tx.Commit();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VaultEvent>> ListEventsAsync(string owner, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); using var c = database.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM vault_events WHERE owner=@owner ORDER BY id"; cmd.With("@owner", owner);
        using var r = cmd.ExecuteReader(); var events = new List<VaultEvent>();
        while (r.Read()) events.Add(new(SqliteDatabase.ReadTime(r.GetString("at")), r.GetString("vm"), r.GetString("text"), r.GetBool("warning")));
        return Task.FromResult<IReadOnlyList<VaultEvent>>(events);
    }
}
