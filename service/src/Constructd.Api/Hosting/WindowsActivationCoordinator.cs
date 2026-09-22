using System.Text.RegularExpressions;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Hosting;

public sealed class WindowsActivationCoordinator(WindowsLicenseStore licenses, IClock clock)
{
    public async Task ReconcileAsync(WindowsGuestStatus guest, WindowsGuestReport report,
        IWindowsLicenseMachines provider, IWindowsGuestChannel channel, CancellationToken ct)
    {
        var op = guest.Operation!;
        if (op.Stage is "verified" or "failed")
        { await channel.ClearWindowsKeyAsync(guest.VmName, guest.Incarnation, ct); return; }
        if (report.AllocationId != op.AllocationId) return;
        if (guest.DeliveredAt is { } sent && clock.UtcNow - sent > TimeSpan.FromMinutes(30))
        { await Fail("activation-timeout"); return; }
        if (op.Stage == "acquiring") { await Fail("activation-result-uncertain"); return; }
        if (guest.License?.Status == 1)
        {
            if (guest.License.PartialKey != guest.PartialKey) { await Fail("personal-key-present"); return; }
            await licenses.SetOperationStageAsync(guest, "verified", null, ct);
            await channel.ClearWindowsKeyAsync(guest.VmName, guest.Incarnation, ct); return;
        }
        var observed = report.Operation;
        if (observed is not null && observed.Id == op.Id && observed.AllocationId == op.AllocationId)
        {
            if (observed.Stage == "failed") { await Fail("local-activation-failed"); return; }
            if (op.Stage == "preparing" && observed.Stage == "prepared")
            {
                if (report.PartialKey != guest.PartialKey || !Guid.TryParse(observed.ActivationId, out _) ||
                    observed.ActivationId != guest.License?.ActivationId ||
                    !Regex.IsMatch(observed.InstallationId ?? "", @"\A[0-9]{1,128}\z") ||
                    !Regex.IsMatch(observed.ProductKeyId ?? "", @"\A[0-9-]{1,128}\z"))
                { await Fail("installation-id-invalid"); return; }
                if (op.Mode == "replay")
                {
                    var machine = (await licenses.MachinesAsync(ct)).Single(m => m.Id == guest.MachineId);
                    if (!machine.HasConfirmationId) { await Fail("confirmation-unavailable"); return; }
                    await licenses.SetOperationStageAsync(guest, "applying", null, ct);
                    guest = guest with { Operation = op with { Stage = "applying" } };
                }
                else
                {
                    // Dependencies are checked before charging an attempt. Persist the
                    // uncertain boundary before the only call that can contact Microsoft.
                    if (!await provider.ActivationProviderReadyAsync(ct))
                    { await licenses.SaveAsync(guest with { Error = "vamt-unavailable" }, ct); return; }
                    string kind;
                    try { kind = await licenses.BeginAcquisitionAsync(guest, ct); }
                    catch (ChildValidationException) { await Fail("key-unavailable"); return; }
                    try
                    {
                        var cid = await provider.AcquireConfirmationIdAsync(kind, observed, ct);
                        if (!Regex.IsMatch(cid, @"\A[0-9]{48}\z")) throw new InvalidOperationException();
                        await licenses.StoreConfirmationAsync(guest, observed.InstallationId!, cid, ct);
                        guest = guest with { Operation = op with { Stage = "applying" } };
                    }
                    catch
                    {
                        await licenses.SetOperationStageAsync(guest, "failed", "activation-result-uncertain", CancellationToken.None);
                        await channel.ClearWindowsKeyAsync(guest.VmName, guest.Incarnation, CancellationToken.None);
                        throw;
                    }
                }
            }
        }
        var command = await licenses.ActivationCommandAsync(guest, ct);
        await provider.DeliverActivationAsync(guest.VmName, guest.Incarnation, command, ct);
        async Task Fail(string error)
        {
            await licenses.SetOperationStageAsync(guest, "failed", error, ct);
            await channel.ClearWindowsKeyAsync(guest.VmName, guest.Incarnation, ct);
        }
    }
}
