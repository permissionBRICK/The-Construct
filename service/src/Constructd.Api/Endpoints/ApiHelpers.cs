using System.Security.Claims;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.AspNetCore.Authorization;

namespace Constructd.Api.Endpoints;

/// <summary>Result of resolving a VM route parameter: either the VM, or the response to return.</summary>
internal readonly record struct VmLookup(Vm? Vm, IResult? Failure)
{
    public bool Ok => Vm is not null;
}

/// <summary>Shared endpoint plumbing: VM resolution + authorization, audit writing, DTO mapping.</summary>
internal static class ApiHelpers
{
    /// <summary>
    /// Looks a VM up and authorizes the caller against it. A VM that exists but belongs to someone
    /// else answers 403 (not 404): ownership is not a secret, and a user who mistypes their own VM
    /// name should be able to tell the two cases apart.
    /// </summary>
    public static async Task<VmLookup> ResolveVmAsync(
        HttpContext http,
        IVmRepository vms,
        IAuthorizationService authorization,
        string name,
        string policy,
        CancellationToken cancellationToken)
    {
        if (!VmNameValidator.IsValid(name))
        {
            return new VmLookup(null, Problems.BadRequest($"VM name is invalid: {VmNameValidator.Rule}"));
        }

        var vm = await vms.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            return new VmLookup(null, Problems.NotFound($"No VM named '{name}'."));
        }

        var result = await authorization.AuthorizeAsync(http.User, vm, policy).ConfigureAwait(false);
        return result.Succeeded
            ? new VmLookup(vm, null)
            : new VmLookup(null, Problems.Forbidden($"You may not access VM '{name}'."));
    }

    /// <summary>
    /// Refuses a mutation on a VM whose removal has already been accepted. Reads stay allowed, so a
    /// client can still see what is happening to it.
    /// </summary>
    public static IResult? FenceDeleting(Vm vm) =>
        vm.Deleting
            ? Problems.Conflict($"VM '{vm.Name}' is being deleted.")
            : null;

    public static Task AuditAsync(
        IAuditLog audit,
        IClock clock,
        ClaimsPrincipal user,
        string action,
        string target,
        AuditOutcome outcome,
        string? detail,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(
            new AuditEntry(clock.UtcNow, user.Actor(), action, target, outcome, detail),
            cancellationToken);

    public static async Task<VmResponse> ToResponseAsync(
        Vm vm,
        IPortForwardManager forwards,
        ConstructdOptions options,
        CancellationToken cancellationToken)
    {
        var list = await forwards.ListAsync(vm.Name, cancellationToken).ConfigureAwait(false);
        // ONE public host per VM (plan §4.12): the VM's own rendered name when the host runs a
        // PublicHostPattern, the service's PublicHost otherwise — so every forward of this VM is
        // advertised under the same name its endpoint reports.
        var publicHost = options.PublicHostFor(vm.Name);
        return new VmResponse(
            vm.Name,
            vm.Owner,
            vm.Cpu,
            vm.RamGb,
            vm.DiskGb,
            vm.Created,
            vm.State,
            vm.SshForwardPort,
            publicHost,
            vm.Deleting,
            ToResponse(vm.IdlePolicy, options.Idle, clamped: false),
            [.. list.Select(f => ForwardResponse.From(f, publicHost))]);
    }

    public static IdlePolicyResponse ToResponse(IdlePolicy policy, IdleOptions idle, bool clamped) =>
        new(policy.TimeoutMinutes, policy.Action, idle.MaxTimeoutMinutes, clamped);

    /// <summary>Parses an enum sent as a (case-insensitive) string in a request body.</summary>
    public static bool TryParseEnum<TEnum>(string? value, out TEnum parsed)
        where TEnum : struct, Enum =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out parsed) && Enum.IsDefined(parsed);

    public static string Options<TEnum>()
        where TEnum : struct, Enum =>
        string.Join("|", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()));

    /// <summary>An enum value spelled the way the wire spells it (camelCase strings, ApiJson).</summary>
    public static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        value.ToString()!.ToLowerInvariant();
}
