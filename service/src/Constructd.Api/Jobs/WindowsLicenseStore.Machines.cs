using System.Text.Json;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

public sealed partial class WindowsLicenseStore
{
    public static string Allocation(Vm vm) => vm.Created.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private sealed record Confirmation(string InstallationId, string Value);
    private async Task<T> ChangeAsync<T>(Func<State, T> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { var state = Read(); var result = change(state); Write(state); return result; }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task<WindowsLicenseMachine[]> MachinesAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return Read().Machines.ToArray(); }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public Task<WindowsLicenseMachine?> ReserveMachineAsync(Vm vm, CancellationToken ct) => ChangeAsync<WindowsLicenseMachine?>(s =>
    {
        var allocation = Allocation(vm);
        var existing = s.Machines.Find(m => m.AllocationId == allocation && m.VmName == vm.Name);
        if (existing is not null) return existing;
        var machine = s.Machines.Find(m => m.State == "available" && m.HostId == cipher.HostId &&
            !m.PreviousNames.Contains(vm.Name, StringComparer.OrdinalIgnoreCase) &&
            JsonSerializer.Serialize(m.Hardware) == JsonSerializer.Serialize(vm.Hardware));
        if (machine is null) return null;
        var reserved = machine with { State = "reserved", VmName = vm.Name, AllocationId = allocation,
            PreviousNames = [..machine.PreviousNames, vm.Name] };
        s.Machines[s.Machines.IndexOf(machine)] = reserved;
        return reserved;
    }, ct);
    public Task<WindowsLicenseMachine?> BeginCleaningAsync(Vm vm, CancellationToken ct) => ChangeAsync<WindowsLicenseMachine?>(s =>
    {
        var machine = s.Machines.Find(m => m.VmName == vm.Name && m.AllocationId == Allocation(vm));
        if (machine is null) return null;
        if (vm.Incarnation is not null && vm.Incarnation != machine.Incarnation) throw new ChildValidationException("vm-incarnation-conflict", "vm");
        var cleaning = machine with { State = "cleaning" };
        s.Machines[s.Machines.IndexOf(machine)] = cleaning;
        return cleaning;
    }, ct);
    public Task FinishCleaningAsync(string id, CancellationToken ct) => ChangeAsync(s =>
    {
        var index = s.Machines.FindIndex(m => m.Id == id && m.State == "cleaning");
        if (index >= 0) s.Machines[index] = s.Machines[index] with { State = "available", VmName = null, AllocationId = null };
        return true;
    }, ct);
    public Task<WindowsLicenseMachine[]> BeginRetirementAsync(string keyId, CancellationToken ct) => ChangeAsync(s =>
    {
        if (s.Guests.Any(g => g.KeyId == keyId && !g.Released) || s.Machines.Any(m => m.KeyId == keyId && m.State is not ("available" or "retiring")))
            throw new ChildValidationException("key-in-use", "key");
        var keyIndex = s.Keys.FindIndex(k => k.Info.Id == keyId);
        if (keyIndex >= 0) s.Keys[keyIndex] = s.Keys[keyIndex] with { Retiring = true };
        for (var i = 0; i < s.Machines.Count; i++) if (s.Machines[i].KeyId == keyId) s.Machines[i] = s.Machines[i] with { State = "retiring" };
        return s.Machines.Where(m => m.KeyId == keyId).ToArray();
    }, ct);
    public Task FinishRetirementAsync(string id, CancellationToken ct) => ChangeAsync(s =>
    {
        s.Machines.RemoveAll(m => m.Id == id && m.State == "retiring"); s.Confirmations.Remove(id); return true;
    }, ct);
    public Task<WindowsGuestStatus> AuthorizeActivationAsync(string name, string incarnation, string operationId, string actor, CancellationToken ct) => ChangeAsync(s =>
    {
        var index = s.Guests.FindIndex(g => g.VmName == name && g.Incarnation == incarnation && !g.Released);
        if (index < 0) throw new ChildValidationException("vm-incarnation-conflict", "vm");
        var guest = s.Guests[index];
        if (guest.Operation is not { Stage: "failed" or "verified" } op || op.Id != operationId || guest.MachineId is null)
            throw new ChildValidationException("activation-operation-conflict", "operation");
        if (guest.License?.Status == 1) throw new ChildValidationException("guest-already-activated", "vm");
        s.Guests[index] = guest with { Operation = new(Guid.NewGuid().ToString("n"), op.AllocationId, "initial", "waiting"),
            Activation = "assigned", Attempted = false, DeliveredAt = null, Error = null };
        // The endpoint's audit records the actor. No Microsoft request occurs here.
        return s.Guests[index];
    }, ct);
    public Task<WindowsActivationCommand> ActivationCommandAsync(WindowsGuestStatus guest, CancellationToken ct) => ChangeAsync(s =>
    {
        var index = GuestIndex(s, guest); guest = s.Guests[index];
        var op = guest.Operation!;
        var key = s.Keys.Single(k => k.Info.Id == guest.KeyId);
        string? cid = null;
        if (op.Stage == "applying")
        {
            if (!s.Confirmations.TryGetValue(guest.MachineId!, out var encrypted)) throw new ChildValidationException("confirmation-unavailable", "key");
            cid = JsonSerializer.Deserialize<Confirmation>(cipher.Decrypt(encrypted))!.Value;
        }
        if (op.Stage == "waiting") s.Guests[index] = guest with { Operation = op with { Stage = "preparing" }, DeliveredAt = clock.UtcNow };
        return new WindowsActivationCommand(op.Id, op.AllocationId, op.Stage == "applying" ? "apply" : "prepare", cipher.Decrypt(key.Ciphertext), cid);
    }, ct);
    public Task<string> BeginAcquisitionAsync(WindowsGuestStatus guest, CancellationToken ct) => ChangeAsync(s =>
    {
        var index = GuestIndex(s, guest); guest = s.Guests[index]; var op = guest.Operation!;
        if (op.Stage != "preparing" || op.Mode != "initial") throw new ChildValidationException("activation-operation-conflict", "operation");
        var keyIndex = s.Keys.FindIndex(k => k.Info.Id == guest.KeyId); var key = s.Keys[keyIndex];
        if (key.Info.Kind == "mak")
        {
            var reserved = s.Guests.Count(g => g.KeyId == key.Info.Id && !g.Released && !g.Attempted && g.Operation?.Mode != "replay");
            if (key.Info.Used + reserved > key.Info.Budget) throw new ChildValidationException("key-unavailable", "key");
        }
        s.Keys[keyIndex] = key with { Info = key.Info with { Used = key.Info.Used + 1 } };
        s.Guests[index] = guest with { Attempted = true, Operation = op with { Stage = "acquiring" } };
        return key.Info.Kind;
    }, ct);
    public Task StoreConfirmationAsync(WindowsGuestStatus guest, string installationId, string confirmationId, CancellationToken ct) => ChangeAsync(s =>
    {
        var index = GuestIndex(s, guest); var current = s.Guests[index];
        if (current.Operation?.Stage != "acquiring") throw new ChildValidationException("activation-operation-conflict", "operation");
        s.Confirmations[current.MachineId!] = cipher.Encrypt(JsonSerializer.Serialize(new Confirmation(installationId, confirmationId)));
        var machine = s.Machines.FindIndex(m => m.Id == current.MachineId);
        s.Machines[machine] = s.Machines[machine] with { HasConfirmationId = true };
        s.Guests[index] = current with { Operation = current.Operation with { Stage = "applying" } }; return true;
    }, ct);
    public Task SetOperationStageAsync(WindowsGuestStatus guest, string stage, string? error, CancellationToken ct) => ChangeAsync(s =>
    {
        var index = GuestIndex(s, guest); var current = s.Guests[index];
        if (current.Operation?.Stage == "verified" && stage == "verified") return true;
        s.Guests[index] = current with { Operation = current.Operation! with { Stage = stage, Error = error }, Error = error,
            Activation = stage == "verified" ? "activated" : stage == "failed" ? "failed" : "assigned" };
        var machine = s.Machines.FindIndex(m => m.Id == current.MachineId);
        if (machine >= 0) s.Machines[machine] = s.Machines[machine] with {
            State = stage == "failed" ? "needs activation" : "assigned",
            Reuses = s.Machines[machine].Reuses + (stage == "verified" && current.Operation?.Mode == "replay" ? 1 : 0) };
        return true;
    }, ct);
    private static int GuestIndex(State s, WindowsGuestStatus guest)
    {
        var index = s.Guests.FindIndex(g => g.VmName == guest.VmName && g.Incarnation == guest.Incarnation && !g.Released &&
            g.AllocationId == guest.AllocationId && g.Operation?.Id == guest.Operation?.Id);
        if (index < 0) throw new ChildValidationException("activation-operation-conflict", "operation");
        return index;
    }
}
