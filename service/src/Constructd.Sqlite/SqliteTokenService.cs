using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.Data.Sqlite;

namespace Constructd.Sqlite;

/// <summary>
/// Durable <see cref="ITokenService"/>. Only SHA-256 hashes reach the database — the plaintext exists
/// in the issuing response and nowhere else, which the persistence tests assert against the raw file.
/// </summary>
public sealed class SqliteTokenService(SqliteDatabase database, IClock clock, IUserStore users, IVmRepository vms)
    : ITokenService
{
    public async Task<IssuedToken> IssueAsync(string userName, string label, CancellationToken cancellationToken)
    {
        var plaintext = TokenHasher.GenerateSecret();
        var token = await StoreAsync(userName, label, plaintext, cancellationToken).ConfigureAwait(false);
        return new IssuedToken(token, plaintext);
    }

    public Task<ApiToken> ImportAsync(
        string userName,
        string label,
        string plaintext,
        CancellationToken cancellationToken) =>
        StoreAsync(userName, label, plaintext, cancellationToken);

    public async Task<string> IssueVmTokenAsync(string vmName, CancellationToken cancellationToken)
    {
        var vm = await vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false)
                 ?? throw new InvalidOperationException($"Unknown VM '{vmName}'.");

        var plaintext = TokenHasher.GenerateSecret();
        await vms.UpdateAsync(vm with { VmTokenHash = TokenHasher.Hash(plaintext) }, cancellationToken)
            .ConfigureAwait(false);

        return plaintext;
    }

    public async Task<TokenPrincipal?> ValidateAsync(string plaintext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return null;
        }

        var hash = TokenHasher.Hash(plaintext);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);

        string? userName = null;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT user_name FROM tokens WHERE token_hash = @hash;";
            lookup.With("@hash", hash);
            userName = (string?)await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        if (userName is not null)
        {
            // An orphaned token (user deleted) authenticates nobody.
            var user = await users.GetAsync(userName, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return null;
            }

            await using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE tokens SET last_used = @now WHERE token_hash = @hash;";
            touch.With("@now", SqliteDatabase.Text(clock.UtcNow)).With("@hash", hash);
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return new TokenPrincipal(TokenKind.User, user.Name, user.Role, VmName: null);
        }

        await using var vmLookup = connection.CreateCommand();
        vmLookup.CommandText = "SELECT name FROM vms WHERE vm_token_hash = @hash;";
        vmLookup.With("@hash", hash);
        var vmName = (string?)await vmLookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return vmName is null
            ? null
            : new TokenPrincipal(TokenKind.Vm, $"vm:{vmName}", Role.User, vmName);
    }

    public async Task<IReadOnlyList<ApiToken>> ListAsync(string userName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tokens WHERE user_name = @userName ORDER BY created;";
        command.With("@userName", userName);

        var tokens = new List<ApiToken>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tokens.Add(Read(reader));
        }

        return tokens;
    }

    public async Task<int> RevokeAllAsync(string userName, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tokens WHERE user_name = @userName;";
        command.With("@userName", userName);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiToken> StoreAsync(
        string userName,
        string label,
        string plaintext,
        CancellationToken cancellationToken)
    {
        var token = new ApiToken(
            Id: Guid.NewGuid().ToString("n"),
            UserName: userName,
            TokenHash: TokenHasher.Hash(plaintext),
            Created: clock.UtcNow,
            LastUsed: null,
            Label: label);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tokens (id, user_name, token_hash, created, last_used, label)
            VALUES (@id, @userName, @tokenHash, @created, NULL, @label)
            ON CONFLICT(token_hash) DO NOTHING;
            """;
        command
            .With("@id", token.Id)
            .With("@userName", token.UserName)
            .With("@tokenHash", token.TokenHash)
            .With("@created", SqliteDatabase.Text(token.Created))
            .With("@label", token.Label);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return token;
    }

    private static ApiToken Read(SqliteDataReader reader) => new(
        reader.GetString("id"),
        reader.GetString("user_name"),
        reader.GetString("token_hash"),
        SqliteDatabase.ReadTime(reader.GetString("created")),
        SqliteDatabase.ReadTimeOrNull(reader.GetStringOrNull("last_used")),
        reader.GetString("label"));
}
