using System.Security.Cryptography;
using System.Text;
using Constructd.Store.Records;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Store.Http;

/// <summary>
/// The store on its own (5.4): the same routes as in constructd under <c>/api/v1/store</c>, one
/// SQLite file, plain HTTP behind the operator's TLS proxy.
///
/// Until pairing and store tokens exist (8.3), its participants are configured: each entry of
/// <c>Store:Participants</c> names a bearer token and the account, kind and id it stands for.
/// </summary>
public static class StandaloneStore
{
    public const string DefaultDatabasePath = "/data/store.db";

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        var participants = builder.Configuration.GetSection("Store:Participants").Get<List<StoreParticipant>>() ?? [];
        builder.Services.AddSingleton<IStorePrincipalResolver>(new TokenPrincipalResolver(participants));
        builder.Services.AddRecordStore(builder.Configuration["Store:DatabasePath"] ?? DefaultDatabasePath);

        var app = builder.Build();
        app.MapGet("/healthz", () => Results.Ok());
        app.MapGroup("/api/v1/store").MapRecordStore();
        return app;
    }
}

/// <summary>A configured participant of the stand-alone store.</summary>
public sealed class StoreParticipant
{
    public string Token { get; set; } = "";
    public string Account { get; set; } = "";
    public ParticipantKind Kind { get; set; }
    public string Id { get; set; } = "";
}

/// <summary>Maps <c>Authorization: Bearer &lt;token&gt;</c> to a configured participant.</summary>
public sealed class TokenPrincipalResolver(IEnumerable<StoreParticipant> participants) : IStorePrincipalResolver
{
    private readonly List<(byte[] Hash, StorePrincipal Principal)> _participants = participants
        .Where(p => p.Token.Length > 0 && p.Account.Length > 0 && p.Id.Length > 0)
        .Select(p => (Hash(p.Token), new StorePrincipal(p.Account, p.Kind, p.Id)))
        .ToList();

    public StorePrincipal? Resolve(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var presented = Hash(header["Bearer ".Length..].Trim());
        return _participants.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(p.Hash, presented)).Principal;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
