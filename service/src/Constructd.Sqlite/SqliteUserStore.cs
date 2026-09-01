using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Sqlite;

/// <summary>Durable <see cref="IUserStore"/>.</summary>
public sealed class SqliteUserStore(SqliteDatabase database) : IUserStore
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
            INSERT OR IGNORE INTO users (name, role, max_vms, created, allow_host_forwards)
            VALUES (@name, @role, @maxVms, @created, @allowHostForwards);
            """;
        command
            .With("@name", user.Name)
            .With("@role", user.Role.ToString())
            .With("@maxVms", user.MaxVms)
            .With("@created", SqliteDatabase.Text(user.Created))
            .With("@allowHostForwards", user.AllowHostForwards ? 1 : 0);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
               SET role = @role, max_vms = @maxVms, allow_host_forwards = @allowHostForwards
             WHERE name = @name;
            """;
        command
            .With("@name", user.Name)
            .With("@role", user.Role.ToString())
            .With("@maxVms", user.MaxVms)
            .With("@allowHostForwards", user.AllowHostForwards ? 1 : 0);

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
        reader.GetBool("allow_host_forwards"));
}
