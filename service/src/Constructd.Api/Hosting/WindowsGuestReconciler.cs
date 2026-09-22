using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Hosting;

public sealed class WindowsGuestReconciler(WindowsLicenseStore licenses, IChildVmDriver driver, IVmRepository vms,
    IVmOperationGate gates, IMediaStore media, IMediaGate mediaGate, IMaintenanceGate maintenance, IClock clock,
    IJobStore jobs, ConstructdOptions options, ILogger<WindowsGuestReconciler> logger, WindowsActivationCoordinator activation) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Fake) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Windows guest reconciliation will retry."); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (driver is not IWindowsGuestChannel channel) return;
        foreach (var listed in (await licenses.SnapshotAsync(ct)).Guests.Where(g => !g.Released))
        {
            using var activity = maintenance.TryEnter("windows-guest", "windows:" + listed.Incarnation, listed.VmName);
            if (activity is null) return;
            await using var gate = await gates.TryAcquireAsync(listed.VmName, "windows:" + listed.Incarnation, ct);
            if (gate is null) continue;
            var vm = await vms.GetAsync(listed.VmName, ct);
            if (vm is null) { await licenses.ReleaseAsync(listed.VmName, listed.Incarnation, ct); continue; }
            if (vm.Deleting || vm.Incarnation != listed.Incarnation) continue;
            if (vm.CurrentJobId is { } job && await jobs.GetAsync(job, ct) is { State: JobState.Queued or JobState.Running }) continue;
            var guest = (await licenses.SnapshotAsync(ct)).Guests.Single(g => g.VmName == listed.VmName && g.Incarnation == listed.Incarnation);
            try
            {
                if (await driver.GetVmIdAsync(vm.Name, ct) != guest.Incarnation) throw new ChildValidationException("vm-incarnation-conflict", "vm");
                var observation = await channel.ObserveWindowsAsync(vm.Name, guest.Incarnation, ct);
                if (options.IsProxmox && !guest.InstallEjected && guest.Uptime is > 0 && observation.Uptime is >= 0 && observation.Uptime < guest.Uptime)
                {
                    await Eject(true); guest = guest with { InstallEjected = true };
                }
                if (observation.Uptime is >= 0) guest = guest with { Uptime = observation.Uptime };
                if (observation.Report is { FirstLogonDone: true } report && (guest.AllocationId is null || report.AllocationId == guest.AllocationId))
                {
                    // Guest-controlled strings are never copied into audit, error or product fields.
                    var match = report.Product == guest.Product && report.Edition == guest.Edition;
                    if (!match)
                    {
                        await channel.ClearWindowsKeyAsync(vm.Name, guest.Incarnation, ct);
                        guest = guest with { Error = "guest-product-edition-mismatch", Activation = "failed" };
                    }
                    else
                    {
                        var license = report.License;
                        if (license is not null && license.ObservedAt <= clock.UtcNow.AddMinutes(5) &&
                            license.ObservedAt >= (guest.License?.ObservedAt ?? DateTimeOffset.MinValue))
                        {
                            guest = guest with { License = license with {
                                ActivationId = Guid.TryParse(license.ActivationId, out var activationId) ? activationId.ToString() : null,
                                PartialKey = license.PartialKey is { Length: 5 } partial && partial.All(char.IsAsciiLetterOrDigit) ? partial : null,
                                Channel = license.Channel is "Retail" or "OEM:DM" or "OEM:SLP" or "Volume:MAK" or "Volume:GVLK" or "Volume:CSVLK" ? license.Channel : null,
                                Status = license.Status is >= 0 and <= 6 ? license.Status : null,
                                GraceMinutes = license.GraceMinutes is >= 0 ? license.GraceMinutes : null,
                                EvaluationEnd = report.Evaluation && license.EvaluationEnd?.Year is > 2000 and < 9999 ? license.EvaluationEnd : null
                            } };
                        }
                        guest = guest with { Stage = "installed", GuestReported = true, Kms = report.Kms,
                            Evaluation = report.Evaluation, Error = report.Evaluation ? "evaluation-media-requires-conversion" : guest.Error };
                        if (!guest.AuxiliaryEjected) { await Eject(false); guest = guest with { InstallEjected = true, AuxiliaryEjected = true }; }
                        if (guest.Operation is null && guest.KeyId is not null && guest.Attempted && report.Activation is "activated" or "failed")
                        {
                            var verified = report.PartialKey == guest.PartialKey;
                            guest = guest with { Activation = verified ? report.Activation : "failed", Error = verified ? null : "partial-key-mismatch" };
                            await channel.ClearWindowsKeyAsync(vm.Name, guest.Incarnation, ct);
                        }
                        else if (guest.KeyId is null && report.Activation == "activated") guest = guest with { Activation = "activated" };
                        await licenses.SaveAsync(guest, ct);
                        if (guest.KeyId is null && guest.Activation != "activated") guest = await licenses.AssignAsync(guest, null, "system", ct);
                        if (guest.Operation is not null && driver is IWindowsLicenseMachines provider)
                        {
                            await activation.ReconcileAsync(guest, report, provider, channel, ct);
                            continue; // The coordinator persisted its operation; do not overwrite it with this snapshot.
                        }
                        if (guest.KeyId is not null && guest.Activation == "assigned" && !guest.Attempted)
                        {
                            var delivery = await licenses.BeginActivationAsync(guest, ct);
                            guest = delivery.Guest with { DeliveredAt = clock.UtcNow };
                            await licenses.SaveAsync(guest, ct);
                            if (delivery.Key is not null) await channel.DeliverWindowsKeyAsync(vm.Name, guest.Incarnation, delivery.Key, ct);
                        }
                    }
                }
                if (guest.Attempted && guest.Activation == "assigned" && guest.DeliveredAt is { } delivered && clock.UtcNow - delivered > TimeSpan.FromMinutes(30))
                {
                    await channel.ClearWindowsKeyAsync(vm.Name, guest.Incarnation, ct);
                    guest = guest with { Activation = "failed", Error = "activation-timeout" };
                }
                await licenses.SaveAsync(guest, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { logger.LogWarning("Windows guest observation or media ejection remains pending for {Vm}.", vm.Name); }

            async Task Eject(bool installOnly)
            {
                await channel.EjectWindowsMediaAsync(vm.Name, guest.Incarnation, installOnly, ct);
                foreach (var reference in await media.ListReferencesForVmAsync(vm.Name, ct))
                {
                    if (installOnly && reference.Slot != MediaSlot.Install) continue;
                    await using var held = await mediaGate.AcquireAsync(reference.MediaId, "windows-eject", ct);
                    await media.RemoveReferenceAsync(reference.MediaId, vm.Name, reference.Slot, ct);
                }
            }
        }
    }
}
