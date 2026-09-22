using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Endpoints;

public static class WindowsLicenseEndpoints
{
    public sealed record AddKeyRequest(string Product, string Edition, string Kind, string Key, int? Budget = null, string? Notes = null);
    public sealed record ReactivateRequest(string Incarnation, string OperationId);
    public sealed record AssignKeyRequest(string Incarnation, string KeyId);
    public static void MapWindowsLicenseEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/host/windows").RequireAuthorization(Policies.Admin);
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ChildValidationException e) { return CodedProblems.Create(e.Code == "validation" ? 400 : 409, e.Code, e.Message, e.Field); }
        });
        group.MapGet("", async (WindowsLicenseStore store, CancellationToken ct) => { var s = await store.SnapshotAsync(ct); return Results.Ok(new { keys = s.Keys, guests = s.Guests, machines = await store.MachinesAsync(ct) }); });
        group.MapPost("/keys", async (AddKeyRequest input, WindowsLicenseStore store, HttpContext http, CancellationToken ct) =>
            Results.Ok(await store.AddAsync(input.Product, input.Edition, input.Kind, input.Key, input.Budget, input.Notes, http.User.Actor(), ct))).Audited("windows-key.add");
        group.MapDelete("/keys/{id}", async (string id, WindowsLicenseStore store, IChildVmDriver driver, IMaintenanceGate maintenance, HttpContext http, CancellationToken ct) =>
        {
            using var activity = maintenance.TryEnter("windows-retire", http.TraceIdentifier, id);
            if (activity is null) return CodedProblems.Create(503, "maintenance", "The host is in maintenance.");
            var machines = await store.BeginRetirementAsync(id, ct);
            if (machines.Length > 0 && driver is not IWindowsLicenseMachines) return CodedProblems.Create(409, "unsupported-capability", "This backend cannot retire license machines.");
            foreach (var machine in machines)
            {
                await ((IWindowsLicenseMachines)driver).RetireWindowsAsync(machine.Incarnation, ct);
                await store.FinishRetirementAsync(machine.Id, ct);
            }
            await store.DeleteAsync(id, http.User.Actor(), ct); return Results.NoContent();
        }).Audited("windows-key.delete", "id");
        group.MapPost("/guests/{name}/assign", async (string name, AssignKeyRequest input, WindowsLicenseStore store,
            IVmRepository vms, IVmOperationGate gate, HttpContext http, CancellationToken ct) =>
        {
            await using var held = await gate.AcquireAsync(name, http.TraceIdentifier, ct);
            var vm = await vms.GetAsync(name, ct);
            if (vm is not { Kind: VmKind.Child, Deleting: false } || vm.Incarnation != input.Incarnation) return CodedProblems.Create(409, "vm-incarnation-conflict", "The VM incarnation changed.");
            var guest = (await store.SnapshotAsync(ct)).Guests.FirstOrDefault(g => g.VmName == name && g.Incarnation == input.Incarnation && !g.Released);
            if (guest is null) return CodedProblems.Create(409, "guest-not-ready", "Wait for the first-logon report.");
            return Results.Ok(await store.AssignAsync(guest, input.KeyId, http.User.Actor(), ct));
        }).Audited("windows-key.assign", "name");
        group.MapPost("/guests/{name}/reactivate", async (string name, ReactivateRequest input, WindowsLicenseStore store,
            IVmRepository vms, IVmOperationGate gate, HttpContext http, CancellationToken ct) =>
        {
            await using var held = await gate.AcquireAsync(name, http.TraceIdentifier, ct);
            var vm = await vms.GetAsync(name, ct);
            if (vm is not { Kind: VmKind.Child, Deleting: false } || vm.Incarnation != input.Incarnation)
                return CodedProblems.Create(409, "vm-incarnation-conflict", "The VM incarnation changed.");
            return Results.Ok(await store.AuthorizeActivationAsync(name, input.Incarnation, input.OperationId, http.User.Actor(), ct));
        }).Audited("windows-key.reactivate", "name");
        api.MapGet("/vms/{name}/windows", async (string name, WindowsLicenseStore store, IVmRepository vms, HttpContext http, CancellationToken ct) =>
        {
            var vm = await vms.GetAsync(name, ct); if (vm is null) return Results.NotFound();
            var owner = http.User.IsPrimaryToken() ? (await vms.GetAsync(http.User.VmTokenName()!, ct))?.Owner : http.User.NameOrEmpty();
            if (!http.User.IsAdmin() && !Ownership.SameName(owner, vm.Owner)) return Results.NotFound();
            return Results.Ok((await store.SnapshotAsync(ct)).Guests.FirstOrDefault(g => g.VmName == name && g.Incarnation == vm.Incarnation && !g.Released));
        }).RequireAuthorization(Policies.UserOrPrimaryToken);
    }
}
