using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Media;
namespace Constructd.Api.Jobs;

/// <summary>One atomic encrypted pool file; only masked projections leave this service.</summary>
public sealed class WindowsLicenseStore(string path, WindowsKeyCipher cipher, IAuditLog audit, IClock clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private sealed record Key(WindowsKeyInfo Info, string Ciphertext);
    private sealed record State(List<Key> Keys, List<WindowsGuestStatus> Guests);
    private FileStream? fileLock;
    private State Read()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // An offline admin CLI and the service must never overwrite each other's pool state.
        fileLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new IOException("Invalid Windows license store.") : new([], []);
    }
    private void Write(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options))
            { JsonSerializer.Serialize(file, state); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
    public async Task<(WindowsKeyInfo[] Keys, WindowsGuestStatus[] Guests)> SnapshotAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct); try { var s = Read(); return (s.Keys.Select(k => k.Info).ToArray(), s.Guests.ToArray()); } finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task<WindowsKeyInfo> AddAsync(string product, string edition, string kind, string value, int? budget, string? notes, string actor, CancellationToken ct)
    {
        _ = WindowsUnattendRenderer.Parse(product + "-" + edition);
        if (kind is not ("retail" or "mak" or "kms-client") || !Regex.IsMatch(value, @"\A[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\z") ||
            kind == "mak" && budget is not > 0 || kind != "mak" && budget is not null || (notes?.Length ?? 0) > 1024) throw new ChildValidationException("validation", "key");
        await gate.WaitAsync(ct);
        try
        {
            var s = Read();
            if (s.Keys.Any(k => cipher.Decrypt(k.Ciphertext) == value)) throw new ChildValidationException("key-exists", "key");
            var info = new WindowsKeyInfo(Guid.NewGuid().ToString("n"), product, edition, kind, value[^5..], budget, 0, notes ?? "");
            s.Keys.Add(new(info, cipher.Encrypt(value))); Write(s);
            await Audit(actor, "windows-key.add", info.Id); return info;
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task DeleteAsync(string id, string actor, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var s = Read(); if (s.Guests.Any(g => g.KeyId == id && !g.Released)) throw new ChildValidationException("key-in-use", "key");
            if (s.Keys.RemoveAll(k => k.Info.Id == id) > 0) { Write(s); await Audit(actor, "windows-key.delete", id); }
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task<WindowsGuestStatus> RegisterAsync(Vm vm, CancellationToken ct)
    {
        var selection = WindowsUnattendRenderer.Parse(vm.Hardware!.Windows!);
        await gate.WaitAsync(ct);
        try
        {
            var s = Read(); var existing = s.Guests.Find(g => g.Incarnation == vm.Incarnation && g.VmName == vm.Name);
            if (existing is not null) return existing;
            var guest = new WindowsGuestStatus(vm.Name, vm.Incarnation!, selection.Product, selection.Edition);
            s.Guests.Add(guest); Write(s); return guest;
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task SaveAsync(WindowsGuestStatus guest, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var s = Read(); var index = s.Guests.FindIndex(g => g.VmName == guest.VmName && g.Incarnation == guest.Incarnation);
            if (index < 0 || s.Guests[index].Released) return;
            var old = s.Guests[index]; s.Guests[index] = guest;
            Write(s);
            if (old.Stage != guest.Stage || old.Activation != guest.Activation) await Audit("system", "windows-guest." + guest.Activation, guest.VmName);
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task<WindowsGuestStatus> AssignAsync(WindowsGuestStatus guest, string? keyId, string actor, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var s = Read(); var index = s.Guests.FindIndex(g => g.VmName == guest.VmName && g.Incarnation == guest.Incarnation && !g.Released);
            if (index < 0) throw new ChildValidationException("vm-incarnation-conflict", "vm");
            guest = s.Guests[index];
            if (guest.KeyId is not null) return guest;
            if (guest.Stage != "installed" || guest.Kms) { if (keyId is not null) throw new ChildValidationException("guest-not-ready", "vm"); return guest; }
            bool Available(Key k) => k.Info.Product == guest.Product && k.Info.Edition == guest.Edition &&
                (k.Info.Kind != "retail" || !s.Guests.Any(g => g.KeyId == k.Info.Id && !g.Released)) &&
                (k.Info.Kind != "mak" || k.Info.Used + s.Guests.Count(g => g.KeyId == k.Info.Id && !g.Released && !g.Attempted) < k.Info.Budget);
            var key = s.Keys.FirstOrDefault(k => (keyId is null || k.Info.Id == keyId) && Available(k));
            if (key is null) { if (keyId is not null) throw new ChildValidationException("key-unavailable", "key"); return guest; }
            guest = guest with { KeyId = key.Info.Id, Activation = "assigned", PartialKey = key.Info.PartialKey };
            s.Guests[index] = guest; Write(s); await Audit(actor, "windows-key.assign", guest.VmName); return guest;
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    // Charge a MAK attempt before delivery. An interrupted/uncertain activation never refunds it.
    public async Task<(WindowsGuestStatus Guest, string? Key)> BeginActivationAsync(WindowsGuestStatus guest, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var s = Read(); var index = s.Guests.FindIndex(g => g.VmName == guest.VmName && g.Incarnation == guest.Incarnation && !g.Released);
            if (index < 0) return (guest, null); guest = s.Guests[index];
            var k = s.Keys.FindIndex(k => k.Info.Id == guest.KeyId); if (k < 0) return (guest, null);
            if (!guest.Attempted)
            {
                var key = s.Keys[k]; if (key.Info.Kind == "mak") s.Keys[k] = key with { Info = key.Info with { Used = key.Info.Used + 1 } };
                guest = guest with { Attempted = true, DeliveredAt = clock.UtcNow }; s.Guests[index] = guest; Write(s);
                await Audit("system", "windows-key.deliver", guest.VmName);
            }
            return (guest, cipher.Decrypt(s.Keys[k].Ciphertext));
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    public async Task ReleaseAsync(string name, string incarnation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var s = Read(); var index = s.Guests.FindIndex(g => g.VmName == name && g.Incarnation == incarnation && !g.Released);
            if (index < 0) return;
            s.Guests[index] = s.Guests[index] with { Released = true }; Write(s); await Audit("system", "windows-key.release", name);
        }
        finally { fileLock?.Dispose(); fileLock = null; gate.Release(); }
    }
    private Task Audit(string actor, string action, string target) => audit.AppendAsync(new(clock.UtcNow, actor, action, target, AuditOutcome.Success, null), CancellationToken.None);
}
