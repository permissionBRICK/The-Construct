using Microsoft.Data.Sqlite;

namespace Constructd.Sync.Profiles;

/// <summary>What the engine last saw, or wrote, in one VM's folder for one profile (9.4 <c>vm_profile_state</c>).</summary>
/// <param name="Revision">The revision in the VM's <c>.rev</c> file, null when it had none.</param>
/// <param name="Content">The profile file as decoded JSON text, null when absent.</param>
/// <param name="Fingerprint">See <see cref="VmProfileFolder.Fingerprint"/>.</param>
public sealed record VmProfileState(string Profile, long? Revision, string? Content, string Fingerprint);

/// <summary>
/// The engine's per-VM sync state in its own SQLite file: per profile what the folder held when the
/// engine last read or wrote it (the write-back guard and the "has it changed" test of the folder
/// read), and the last projects digest the VM's heartbeat carried.
/// </summary>
public sealed class VmProfileStates : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public VmProfileStates(string databasePath)
    {
        if (databasePath != ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Run("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS vm_profile_state (
                vm TEXT NOT NULL, profile TEXT NOT NULL, revision INTEGER, content TEXT, fingerprint TEXT NOT NULL,
                PRIMARY KEY (vm, profile));
            CREATE TABLE IF NOT EXISTS vm_projects_digest (vm TEXT PRIMARY KEY, digest TEXT NOT NULL);
            """);
    }

    public IReadOnlyDictionary<string, VmProfileState> Get(string vm)
    {
        lock (_lock)
        {
            using var command = Command("SELECT profile, revision, content, fingerprint FROM vm_profile_state WHERE vm = $vm", ("$vm", vm));
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, VmProfileState>(StringComparer.Ordinal);
            while (reader.Read())
            {
                result[reader.GetString(0)] = new VmProfileState(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3));
            }

            return result;
        }
    }

    public void Set(string vm, VmProfileState state)
    {
        lock (_lock)
        {
            Run("""
                INSERT INTO vm_profile_state (vm, profile, revision, content, fingerprint) VALUES ($vm, $p, $r, $c, $f)
                ON CONFLICT (vm, profile) DO UPDATE SET revision = $r, content = $c, fingerprint = $f
                """, ("$vm", vm), ("$p", state.Profile), ("$r", state.Revision), ("$c", state.Content), ("$f", state.Fingerprint));
        }
    }

    public string? Digest(string vm)
    {
        lock (_lock)
        {
            using var command = Command("SELECT digest FROM vm_projects_digest WHERE vm = $vm", ("$vm", vm));
            return command.ExecuteScalar() as string;
        }
    }

    /// <summary>Records the digest a read answered to; null forgets it, so the next heartbeat reads again.</summary>
    public void SetDigest(string vm, string? digest)
    {
        lock (_lock)
        {
            if (digest is null) Run("DELETE FROM vm_projects_digest WHERE vm = $vm", ("$vm", vm));
            else Run("INSERT INTO vm_projects_digest (vm, digest) VALUES ($vm, $d) ON CONFLICT (vm) DO UPDATE SET digest = $d", ("$vm", vm), ("$d", digest));
        }
    }

    public void Dispose() => _db.Dispose();

    private void Run(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
