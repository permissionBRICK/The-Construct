using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>
/// The service's durable store: one SQLite file under <c>C:\ProgramData\Construct\service\</c> on a
/// real host (plan §4.4). Connections are opened per operation (the driver pools them), with WAL and
/// a busy timeout so the API, the job engine and the idle scheduler can write concurrently.
///
/// Everything here is hand-written SQL — no ORM, and no schema migration machinery yet: the schema
/// is created if missing, and evolving it is a deliberate decision to make when the first change
/// comes.
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }

    /// <summary>
    /// Creates the schema if it is missing. Name columns are <c>COLLATE NOCASE</c> because identities
    /// (<c>DOMAIN\user</c>) and VM names are compared case-insensitively everywhere else too.
    /// </summary>
    public void EnsureCreated()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                name                TEXT PRIMARY KEY COLLATE NOCASE,
                role                TEXT NOT NULL,
                max_vms             INTEGER NOT NULL,
                created             TEXT NOT NULL,
                allow_host_forwards INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tokens (
                id         TEXT PRIMARY KEY,
                user_name  TEXT NOT NULL COLLATE NOCASE,
                token_hash TEXT NOT NULL UNIQUE,
                created    TEXT NOT NULL,
                last_used  TEXT NULL,
                label      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tokens_user ON tokens (user_name);

            CREATE TABLE IF NOT EXISTS vms (
                name                 TEXT PRIMARY KEY COLLATE NOCASE,
                owner                TEXT NOT NULL COLLATE NOCASE,
                cpu                  INTEGER NOT NULL,
                ram_gb               INTEGER NOT NULL,
                disk_gb              INTEGER NOT NULL,
                created              TEXT NOT NULL,
                state                TEXT NOT NULL,
                ssh_forward_port     INTEGER NULL,
                vm_token_hash        TEXT NULL,
                idle_timeout_minutes INTEGER NOT NULL,
                idle_action          TEXT NOT NULL,
                deleting             INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_vms_owner ON vms (owner);
            CREATE INDEX IF NOT EXISTS ix_vms_token ON vms (vm_token_hash);

            CREATE TABLE IF NOT EXISTS activity (
                vm_name     TEXT PRIMARY KEY COLLATE NOCASE,
                busy        INTEGER NOT NULL,
                reasons     TEXT NOT NULL,
                reported_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS forwards (
                id          TEXT PRIMARY KEY,
                vm_name     TEXT NOT NULL COLLATE NOCASE,
                vm_port     INTEGER NOT NULL,
                public_port INTEGER NULL,
                target      TEXT NOT NULL,
                label       TEXT NOT NULL,
                created     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_forwards_vm ON forwards (vm_name);

            CREATE TABLE IF NOT EXISTS audit (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT NOT NULL,
                actor   TEXT NOT NULL,
                action  TEXT NOT NULL,
                target  TEXT NOT NULL,
                outcome TEXT NOT NULL,
                detail  TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS jobs (
                id       TEXT PRIMARY KEY,
                kind     TEXT NOT NULL,
                vm_name  TEXT NULL COLLATE NOCASE,
                owner    TEXT NOT NULL COLLATE NOCASE,
                state    TEXT NOT NULL,
                progress TEXT NOT NULL,
                result   TEXT NULL,
                error    TEXT NULL,
                created  TEXT NOT NULL,
                finished TEXT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    // ---- value conversion ------------------------------------------------------------------

    public static string Text(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    public static object TextOrNull(DateTimeOffset? value) => value is null ? DBNull.Value : Text(value.Value);

    public static DateTimeOffset ReadTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? ReadTimeOrNull(object? value) =>
        value is string text ? ReadTime(text) : null;

    public static TEnum ReadEnum<TEnum>(string value)
        where TEnum : struct, Enum => Enum.Parse<TEnum>(value, ignoreCase: true);
}

/// <summary>Small helpers that keep the SQL call sites readable.</summary>
internal static class SqliteCommandExtensions
{
    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static string GetString(this SqliteDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    public static string? GetStringOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static int GetInt(this SqliteDataReader reader, string column) =>
        reader.GetInt32(reader.GetOrdinal(column));

    public static int? GetIntOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    public static bool GetBool(this SqliteDataReader reader, string column) =>
        reader.GetInt32(reader.GetOrdinal(column)) != 0;
}
