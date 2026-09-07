using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Sqlite;

/// <summary>Durable <see cref="IUserStore"/>.</summary>
public sealed class SqliteUserStore(SqliteDatabase database) : IUserStore, IUserAllowanceStore
{
    public async Task<User?> GetAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM users WHERE name = @name;";
        command.With("@name", name);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM users ORDER BY name;";

        var users = new List<User>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            users.Add(Read(reader));
        }

        return users;
    }

    public async Task<bool> CreateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO users (name, role, max_vms, created, allow_host_forwards, enabled, allow_child_creation, max_retained_children, cpu_budget, ram_budget_bytes, storage_budget_bytes, max_child_lifetime_seconds, allow_never_lifetime, allow_sharing)
            VALUES (@name, @role, @maxVms, @created, @allowHostForwards, @enabled, @allow_child_creation, @max_retained_children, @cpu_budget, @ram_budget_bytes, @storage_budget_bytes, @max_child_lifetime_seconds, @allow_never_lifetime, @allow_sharing);
            """;
        command
            .With("@name", user.Name)
            .With("@role", user.Role.ToString())
            .With("@maxVms", user.MaxVms)
            .With("@created", SqliteDatabase.Text(user.Created))
            .With("@allowHostForwards", user.AllowHostForwards ? 1 : 0)
            .With("@enabled", user.Enabled ? 1 : 0)
            .With("@allow_child_creation", user.Allowance?.AllowChildCreation)
            .With("@max_retained_children", user.Allowance?.MaxRetainedChildren)
            .With("@cpu_budget", user.Allowance?.CpuBudget)
            .With("@ram_budget_bytes", user.Allowance?.RamBudgetBytes)
            .With("@storage_budget_bytes", user.Allowance?.StorageBudgetBytes)
            .With("@max_child_lifetime_seconds", user.Allowance?.MaxChildLifetimeSeconds)
            .With("@allow_never_lifetime", user.Allowance?.AllowNeverLifetime)
            .With("@allow_sharing", user.Allowance?.AllowSharing);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
               SET role = @role, max_vms = @maxVms, allow_host_forwards = @allowHostForwards, enabled = @enabled
             WHERE name = @name;
            """;
        command
            .With("@name", user.Name)
            .With("@role", user.Role.ToString())
            .With("@maxVms", user.MaxVms)
            .With("@allowHostForwards", user.AllowHostForwards ? 1 : 0)
            .With("@enabled", user.Enabled ? 1 : 0);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE name = @name;";
        command.With("@name", name);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static User Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetString("name"),
        SqliteDatabase.ReadEnum<Role>(reader.GetString("role")),
        reader.GetInt("max_vms"),
        SqliteDatabase.ReadTime(reader.GetString("created")),
        reader.GetBool("allow_host_forwards"), reader.GetBool("enabled"),
        new UserAllowance(reader.GetBoolOrNull("allow_child_creation"), reader.GetIntOrNull("max_retained_children"), reader.GetIntOrNull("cpu_budget"), reader.GetLongOrNull("ram_budget_bytes"), reader.GetLongOrNull("storage_budget_bytes"), reader.GetLongOrNull("max_child_lifetime_seconds"), reader.GetBoolOrNull("allow_never_lifetime"), reader.GetBoolOrNull("allow_sharing")));
    public async Task<bool> SetEnabledAsync(string name, bool enabled, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET enabled=@enabled WHERE name=@name";
        command.With("@name", name).With("@enabled", enabled);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
    public async Task<bool> SetAllowanceAsync(string name, UserAllowance allowance, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var tx = connection.BeginTransaction(deferred: false);
        var result = await SetAllowanceInTransaction(connection, tx, name, allowance, ct);
        await tx.CommitAsync(ct); return result;
    }
    internal static async Task<bool> SetAllowanceInTransaction(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, string name, UserAllowance allowance, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "UPDATE users SET allow_child_creation=@allow_child_creation, max_retained_children=@max_retained_children, cpu_budget=@cpu_budget, ram_budget_bytes=@ram_budget_bytes, storage_budget_bytes=@storage_budget_bytes, max_child_lifetime_seconds=@max_child_lifetime_seconds, allow_never_lifetime=@allow_never_lifetime, allow_sharing=@allow_sharing WHERE name=@name";
        command.With("@name", name)
            .With("@allow_child_creation", allowance.AllowChildCreation)
            .With("@max_retained_children", allowance.MaxRetainedChildren)
            .With("@cpu_budget", allowance.CpuBudget)
            .With("@ram_budget_bytes", allowance.RamBudgetBytes)
            .With("@storage_budget_bytes", allowance.StorageBudgetBytes)
            .With("@max_child_lifetime_seconds", allowance.MaxChildLifetimeSeconds)
            .With("@allow_never_lifetime", allowance.AllowNeverLifetime)
            .With("@allow_sharing", allowance.AllowSharing);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
