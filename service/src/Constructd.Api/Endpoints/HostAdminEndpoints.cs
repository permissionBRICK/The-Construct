using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Endpoints;

public static class HostAdminEndpoints
{
    public static RouteGroupBuilder MapHostAdminEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/host/status", StatusAsync).RequireAuthorization(Policies.Admin).WithName("HostStatus");
        api.MapGet("/host/capabilities", CapabilitiesAsync).RequireAuthorization(Policies.UserOrPrimaryToken).WithName("HostCapabilities");
        api.MapGet("/host/config", ConfigAsync).RequireAuthorization(Policies.Admin).WithName("HostConfig");
        api.MapPut("/host/config", UpdateConfigAsync).RequireAuthorization(Policies.Admin).Audited("host.config").WithName("UpdateHostConfig");
        api.MapGet("/users", UsersAsync).RequireAuthorization(Policies.Admin).WithName("ListUsers");
        api.MapGet("/users/{name}", UserAsync).RequireAuthorization(Policies.Admin).WithName("GetUser");
        api.MapPut("/users/{name}", UpdateUserAsync).RequireAuthorization(Policies.Admin).Audited("user.update").WithName("UpdateUser");
        api.MapGet("/users/{name}/allowance", AllowanceAsync).RequireAuthorization(Policies.Admin).WithName("GetUserAllowance");
        api.MapPut("/users/{name}/allowance", UpdateAllowanceAsync).RequireAuthorization(Policies.Admin).Audited("user.allowance").WithName("UpdateUserAllowance");
        api.MapGet("/users/{name}/tokens", TokensAsync).RequireAuthorization(Policies.Admin).WithName("ListUserTokens");
        api.MapDelete("/users/{name}/tokens/{id}", RevokeTokenAsync).RequireAuthorization(Policies.Admin).Audited("token.revoke").WithName("RevokeUserToken");
        return api;
    }
    internal static async Task<EffectiveAllowanceResponse> EffectiveAsync(string owner, string? parent, IDelegationPolicy policy, CancellationToken ct) =>
        EffectiveAllowanceResponse.From(await policy.ResolveAsync(owner, parent, ct), await policy.UsageAsync(owner, ct));
    internal static async Task<UserDetailResponse> DetailAsync(User user, IDelegationPolicy policy, ITokenService tokens, CancellationToken ct)
    {
        var effective = await EffectiveAsync(user.Name, null, policy, ct);
        return new(user.Name, user.Role, user.Enabled, user.MaxVms, user.AllowHostForwards, user.Created, user.Allowance ?? UserAllowance.Unset,
            effective, new(effective.Usage.Primaries, effective.Usage.Children), (await tokens.ListAsync(user.Name, ct)).Count);
    }
    private static async Task<IResult> UsersAsync(IUserStore users, IDelegationPolicy policy, ITokenService tokens, CancellationToken ct)
    {
        var result = new List<UserDetailResponse>(); foreach (var user in await users.ListAsync(ct)) result.Add(await DetailAsync(user, policy, tokens, ct));
        return Results.Ok(result);
    }
    private static async Task<IResult> UserAsync(string name, IUserStore users, IDelegationPolicy policy, ITokenService tokens, CancellationToken ct) =>
        await users.GetAsync(name, ct) is { } user ? Results.Ok(await DetailAsync(user, policy, tokens, ct)) : Problems.NotFound("Unknown user.");
    private static async Task<IResult> UpdateUserAsync(string name, UserUpdateRequest request, HttpContext http, IUserStore users,
        IDelegationPolicy policy, ITokenService tokens, IConsoleSessionStore sessions, IVmRepository vms, IVmOperationGate gate, CancellationToken ct)
    {
        await using var handle = await gate.AcquireAsync("$users", http.TraceIdentifier, ct);
        var user = await users.GetAsync(name, ct); if (user is null) return Problems.NotFound("Unknown user.");
        var role = user.Role;
        if (request.Role is not null && !ApiHelpers.TryParseEnum(request.Role, out role)) return CodedProblems.Validation("role", "Expected admin or user.");
        if (request.MaxVms < 0) return CodedProblems.Validation("maxVms", "Must be non-negative.");
        var enabled = request.Enabled ?? user.Enabled;
        if (Ownership.SameName(name, http.User.NameOrEmpty()) && (!enabled || role != Role.Admin)) return CodedProblems.Create(409, "self-demotion", "An admin cannot demote or disable their own account.");
        if (user is { Enabled: true, Role: Role.Admin } && (!enabled || role != Role.Admin) &&
            !(await users.ListAsync(ct)).Any(u => u.Enabled && u.Role == Role.Admin && !Ownership.SameName(u.Name, name)))
            return CodedProblems.Create(409, "last-admin", "The last enabled admin cannot be removed.");
        user = user with { Role = role, Enabled = enabled, MaxVms = request.MaxVms ?? user.MaxVms, AllowHostForwards = request.AllowHostForwards ?? user.AllowHostForwards };
        if (!await users.UpdateAsync(user, ct)) return Problems.NotFound("Unknown user.");
        if (!enabled) { sessions.RemoveForPrincipal(user.Name); foreach (var vm in await vms.ListAsync(user.Name, ct)) sessions.RemoveForPrincipal("vm:" + vm.Name); }
        CodedProblems.Audit(http, "user.update", user.Name, target: user.Name);
        return Results.Ok(await DetailAsync(user, policy, tokens, ct));
    }
    private static async Task<IResult> AllowanceAsync(string name, IUserStore users, IDelegationPolicy policy, CancellationToken ct) =>
        await users.GetAsync(name, ct) is { } user ? Results.Ok(new { stored = user.Allowance ?? UserAllowance.Unset, effective = await EffectiveAsync(name, null, policy, ct) }) : Problems.NotFound("Unknown user.");
    private static async Task<IResult> UpdateAllowanceAsync(string name, UserAllowance request, HttpContext http, IUserStore users, IUserAllowanceStore store, IDelegationPolicy policy, CancellationToken ct)
    {
        if (HostConfigValidation.Allowance(request) is { } error) return CodedProblems.Validation("allowance", error);
        if (!await store.SetAllowanceAsync(name, request, ct)) return Problems.NotFound("Unknown user.");
        CodedProblems.Audit(http, "user.allowance", name, target: name);
        return await AllowanceAsync(name, users, policy, ct);
    }
    private static async Task<IResult> TokensAsync(string name, IUserStore users, ITokenService tokens, CancellationToken ct) =>
        await users.GetAsync(name, ct) is null ? Problems.NotFound("Unknown user.") : Results.Ok((await tokens.ListAsync(name, ct)).Select(t => new { t.Id, t.Label, t.Created, t.LastUsed }));
    private static async Task<IResult> RevokeTokenAsync(string name, string id, HttpContext http, IUserTokenRevoker tokens, CancellationToken ct)
    {
        CodedProblems.Audit(http, "token.revoke", name, target: id);
        return await tokens.RevokeAsync(name, id, ct) ? Results.NoContent() : Problems.NotFound("Unknown token.");
    }
    private static async Task<IResult> CapabilitiesAsync(ICapabilityAggregator aggregator, IHostConfigStore config, ConstructdOptions options, CancellationToken ct)
    {
        var caps = await aggregator.GetAsync(ct); var network = await config.GetAsync<NetworkConfig>("network", ct) ?? HostAdminDefaults.Network;
        var defaults = await config.GetAsync<UserDefaultsConfig>("userDefaults", ct) ?? HostAdminDefaults.UserDefaults;
        return Results.Ok(new
        {
            backend = caps.Backend,
            capabilities = caps,
            policy = new
            {
                network.HostForwardsEnabled,
                network.DirectAddressReporting,
                defaults.AllowNeverLifetime,
                defaults.MaxChildLifetimeSeconds,
                capacityMode = (await CapacityConfigAsync(config, options, ct)).Mode
            }
        });
    }
    internal static async Task<CapacityConfig> CapacityConfigAsync(IHostConfigStore config, ConstructdOptions options, CancellationToken ct) =>
        await config.GetAsync<CapacityConfig>("capacity", ct) ?? HostAdminDefaults.Capacity with { Mode = options.HostAdmin.Capacity.Mode };
    internal static object CapacitySummary(HostCapacitySnapshot c) => new
    {
        c.Epoch,
        c.ObservedAt,
        c.Complete,
        ram = new { totalBytes = c.RamTotalBytes, headroomBytes = c.RamHeadroomBytes, reservedBytes = c.RamReservedBytes, unmanagedBytes = c.RamUnmanagedBytes, physicalFreeBytes = c.RamPhysicalFreeBytes, availableBytes = c.RamAvailableBytes },
        cpu = new { logical = c.CpuLogical, budget = c.CpuBudget, active = c.CpuActive, available = c.CpuAvailable },
        c.Volumes
    };
    private static async Task<IResult> StatusAsync(IReleaseInfo release, ICapacityLedger capacity, IHostConfigStore config, IMaintenanceGate maintenance,
        IJobQueryStore jobs, IVmRepository vms, ConstructdOptions options, CancellationToken ct)
    {
        var snapshot = await capacity.SnapshotAsync(false, ct); var all = await vms.ListAsync(null, ct);
        var marker = await config.GetAsync<MaintenanceMarker>("maintenance", ct);
        var hypervisor = snapshot.Complete ? "ok" : "unreachable";
        return Results.Ok(new
        {
            version = release.Installed,
            health = new { hypervisor, database = "ok", media = Directory.Exists(options.HostAdmin.Media.RootDir) ? "ok" : "missing-root", inventory = snapshot.Complete ? "complete" : "incomplete" },
            capacity = CapacitySummary(snapshot),
            capacityMode = (await CapacityConfigAsync(config, options, ct)).Mode,
            maintenance = new { phase = maintenance.State, since = marker?.Since, updateId = marker?.UpdateId },
            activeJobs = (await jobs.ListAsync(ct)).Where(j => j.State is JobState.Queued or JobState.Running).Select(j => new { j.Id, j.Kind, j.VmName, j.Owner, j.Initiator, j.Phase, j.Created }),
            leaseOverdueCount = all.Count(v => v.Lease is { State: LeaseState.Overdue }),
            unmanagedVmCount = snapshot.Unmanaged.Count
        });
    }
    private static async Task<IResult> ConfigAsync(IHostConfigMetadata metadata, ConstructdOptions options, CancellationToken ct)
    {
        var rows = (await metadata.ListSectionsAsync(ct)).ToDictionary(s => s.Section, StringComparer.Ordinal);
        var result = new Dictionary<string, object>();
        foreach (var (key, fallback) in HostConfigValidation.Defaults)
        {
            rows.TryGetValue(key, out var row);
            object value = row is null ? fallback : JsonSerializer.Deserialize(row.ValueJson, fallback.GetType(), ApiJson.Options)!;
            if (row is null && key == "capacity") value = HostAdminDefaults.Capacity with { Mode = options.HostAdmin.Capacity.Mode };
            if (row is null && key == "updates") value = HostAdminDefaults.Updates with { ManifestPublicKey = options.HostAdmin.Updates.ManifestPublicKey };
            if (value is UpdatesConfig updates) value = HostUpdateTrust.Apply(updates, options);
            // Fields stay at section level; source and updatedAt describe the whole section.
            var fields = JsonSerializer.SerializeToNode(value, ApiJson.Options)!.AsObject();
            fields["source"] = row is null ? "default" : "stored"; fields["updatedAt"] = row?.UpdatedAt;
            result[key] = fields;
        }
        return Results.Ok(result);
    }
    private static async Task<IResult> UpdateConfigAsync(JsonElement request, HttpContext http, IHostConfigMetadata metadata, IClock clock, ConstructdOptions options, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object) return CodedProblems.Validation("config", "Expected an object of sections.");
        var sections = new List<HostConfigSection>(); var expected = new Dictionary<string, DateTimeOffset?>(); var seen = new HashSet<string>();
        foreach (var property in request.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !HostConfigValidation.Defaults.TryGetValue(property.Name, out var fallback) || property.Value.ValueKind != JsonValueKind.Object)
                return CodedProblems.Validation(property.Name, "Unknown, duplicate or invalid configuration section.");
            var allowed = JsonSerializer.SerializeToElement(fallback, ApiJson.Options).EnumerateObject().Select(p => p.Name).ToHashSet();
            var supplied = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in property.Value.EnumerateObject())
                if (!supplied.Add(field.Name) || field.Name != "expectedUpdatedAt" && !allowed.Contains(field.Name)) return CodedProblems.Validation(property.Name + "." + field.Name, "Unknown field.");
            // A replacement must specify every required (non-nullable) property; omitted optional fields become null.
            foreach (var field in fallback.GetType().GetProperties())
                if (field.PropertyType.IsValueType && Nullable.GetUnderlyingType(field.PropertyType) is null &&
                    !property.Value.TryGetProperty(JsonNamingPolicy.CamelCase.ConvertName(field.Name), out _))
                    return CodedProblems.Validation(property.Name + "." + JsonNamingPolicy.CamelCase.ConvertName(field.Name), "Required in a section replacement.");
            object value;
            try
            {
                value = JsonSerializer.Deserialize(property.Value.GetRawText(), fallback.GetType(), ApiJson.Options)!;
                if (property.Value.TryGetProperty("expectedUpdatedAt", out var at)) expected[property.Name] = at.ValueKind == JsonValueKind.Null ? null : at.GetDateTimeOffset();
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return CodedProblems.Validation(property.Name, "Invalid section value."); }
            if (HostConfigValidation.Validate(value) is { } error) return CodedProblems.Validation(property.Name, error);
            if (value is UpdatesConfig updates && HostUpdateTrust.Apply(updates, options) != updates)
                return CodedProblems.Validation("updates", "The update repository, signing key and signature requirement are pinned by the host-local installation.");
            sections.Add(new(property.Name, JsonSerializer.Serialize(value, ApiJson.Options), clock.UtcNow, http.User.Actor()));
        }
        if (sections.Count == 0) return CodedProblems.Validation("config", "At least one section is required.");
        if (!await metadata.TrySetSectionsAsync(sections, expected, ct)) return CodedProblems.Create(409, "config-conflict", "A section changed; reload configuration.");
        CodedProblems.Audit(http, "host.config", http.User.Actor(), target: "host", extra: "sections=" + string.Join("/", sections.Select(s => s.Section)));
        return await ConfigAsync(metadata, options, ct);
    }
}
