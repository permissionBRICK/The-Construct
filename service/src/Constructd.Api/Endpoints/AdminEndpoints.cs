using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

/// <summary>Admin-only surface: users, their tokens and the audit trail.</summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/users", CreateUserAsync)
            .RequireAuthorization(Policies.Admin).Audited("user.create").WithName("CreateUser");

        api.MapDelete("/users/{name}", DeleteUserAsync)
            .RequireAuthorization(Policies.Admin).Audited("user.delete").WithName("DeleteUser");

        api.MapPost("/users/{name}/tokens", IssueTokenAsync)
            .RequireAuthorization(Policies.Admin).Audited("token.issue").WithName("IssueUserToken");

        api.MapGet("/audit", QueryAuditAsync)
            .RequireAuthorization(Policies.Admin).WithName("QueryAudit");

        return api;
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserRequest? request,
        HttpContext http,
        IUserStore users,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var name = request?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Problems.BadRequest("'name' is required.");
        }

        http.SetAuditTarget(name);

        if (!ApiHelpers.TryParseEnum<Role>(request!.Role, out var role))
        {
            return Problems.BadRequest($"'role' must be one of: {ApiHelpers.Options<Role>()}.");
        }

        if (request.MaxVms is not int maxVms || maxVms < 0)
        {
            return Problems.BadRequest("'maxVms' must be a non-negative number.");
        }

        var user = new User(name, role, maxVms, clock.UtcNow, request.AllowHostForwards ?? true);

        if (!await users.CreateAsync(user, cancellationToken).ConfigureAwait(false))
        {
            http.SetAuditDetail("already exists");
            return Problems.Conflict($"User '{name}' already exists.");
        }

        http.SetAuditDetail($"role={role}, maxVms={maxVms}, allowHostForwards={user.AllowHostForwards}");
        return TypedResults.Created($"/api/v1/users/{Uri.EscapeDataString(name)}", UserResponse.From(user));
    }

    private static async Task<IResult> DeleteUserAsync(
        string name,
        HttpContext http,
        IUserStore users,
        IVmRepository vms,
        ITokenService tokens,
        CancellationToken cancellationToken)
    {
        if (Ownership.SameName(name, http.User.NameOrEmpty()))
        {
            http.SetAuditDetail("self-deletion");
            return Problems.Conflict("An admin cannot delete their own account.");
        }

        var owned = await vms.CountByOwnerAsync(name, cancellationToken).ConfigureAwait(false);
        if (owned > 0)
        {
            http.SetAuditDetail($"owns {owned} vm(s)");
            return Problems.Conflict($"User '{name}' still owns {owned} VM(s); delete those first.");
        }

        if (!await users.DeleteAsync(name, cancellationToken).ConfigureAwait(false))
        {
            return Problems.NotFound($"No user named '{name}'.");
        }

        var revoked = await tokens.RevokeAllAsync(name, cancellationToken).ConfigureAwait(false);
        http.SetAuditDetail($"revoked {revoked} token(s)");
        return TypedResults.NoContent();
    }

    private static async Task<IResult> IssueTokenAsync(
        string name,
        CreateTokenRequest? request,
        HttpContext http,
        IUserStore users,
        ITokenService tokens,
        CancellationToken cancellationToken)
    {
        var label = request?.Label?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return Problems.BadRequest("'label' is required so tokens can be told apart later.");
        }

        var user = await users.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Problems.NotFound($"No user named '{name}'.");
        }

        var issued = await tokens.IssueAsync(user.Name, label, cancellationToken).ConfigureAwait(false);

        // The label and id are audited; the secret is not, and is never stored in plaintext.
        http.SetAuditDetail($"id={issued.Token.Id}, label={label}");

        return TypedResults.Created(
            $"/api/v1/users/{Uri.EscapeDataString(user.Name)}/tokens/{issued.Token.Id}",
            new TokenIssuedResponse(issued.Token.Id, issued.Token.Label, issued.Plaintext, issued.Token.Created));
    }

    private static async Task<IResult> QueryAuditAsync(
        int? limit,
        IAuditLog audit,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? options.AuditQueryLimit, 1, 1000);
        var entries = await audit.QueryAsync(take, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(entries.Select(AuditResponse.From).ToList());
    }
}
