using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Authorization;
namespace Constructd.Api.Endpoints;

public static class NetworkEndpoints
{
    public static RouteGroupBuilder MapNetworkEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/vms/{name}/addresses", AddressesAsync).RequireAuthorization(Policies.UserOrPrimaryToken).WithName("GetVmAddresses");
        return api;
    }
    private static async Task<IResult> AddressesAsync(string name, HttpContext http, IVmRepository vms,
        IAuthorizationService authorization, IGuestAddressProvider addresses, IHostNetworkPolicy policy,
        INetworkPolicyReconciler network, ICapabilityAggregator capabilities, CancellationToken ct)
    {
        var lookup = await ApiHelpers.ResolveVmAsync(http, vms, authorization, name, Policies.ChildOperator, ct);
        if (!lookup.Ok) return lookup.Failure!;
        if (lookup.Vm!.Kind != VmKind.Child) return CodedProblems.Create(409, "not-a-child", "Address reporting is a child operation.");
        var hostPolicy = await policy.GetAsync(ct);
        if (!hostPolicy.DirectAddressReporting) return CodedProblems.Create(403, "policy-denied", "Direct address reporting is disabled.");
        var reported = await addresses.GetReportedAddressesAsync(lookup.Vm.Name, ct);
        var backend = (await capabilities.GetAsync(ct)).Network;
        return Results.Ok(new { addresses = reported.Select(a => new { a.Address, a.Family, a.Source, a.ObservedAt, verified = false }),
            isolation = network.IsolationLevel, network = new VmNetworkCapabilities(backend.ClientForward,
                hostPolicy.HostForwardsEnabled && await http.RequestServices.GetRequiredService<IUserStore>().GetAsync(lookup.Vm.Owner, ct) is { AllowHostForwards: true }
                    ? backend.HostForwardChild : CapabilityLevel.Unsupported, backend.AddressVerification) });
    }
}
