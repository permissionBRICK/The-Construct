using System.Globalization;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;

namespace Constructd.Proxmox;

public sealed partial class ProxmoxChildVmPlatform
{
    private async Task<(int Id, JsonElement Config, ChildOwnership Owner)> OffChildAsync(string name, CancellationToken ct)
    {
        var id = await commands.RequireAsync(name, ct);
        if (await commands.StateAsync(id, ct) != VmState.Off) throw new ChildValidationException("vm-not-off", "vm");
        var config = await commands.ConfigAsync(id, ct);
        var owner = ReadOwnership(name) ?? throw new ChildValidationException("artifact-ownership-unverified", "vm");
        if (id != owner.VmId) throw Conflict();
        VerifyOwner(config, owner);
        return (id, config, owner);
    }

    public async Task UpdateHardwareAsync(string name, ChildHardware hardware, bool resendTemplate, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        HardwarePresets.ValidateCapabilities(hardware, await GetCapabilitiesAsync(ct), false);
        var (id, config, owner) = await OffChildAsync(name, ct);
        if (ProxmoxCommands.String(config, "bios") != "ovmf" || hardware.NetworkAttached != config.TryGetProperty("net0", out _))
            throw new ChildValidationException("unsupported-capability", "hardware");
        var slot = ProxmoxChildVmPlatform.DiskSlot(config);
        var disk = DiskBytes(ProxmoxCommands.String(config, slot));
        var requested = (long)hardware.DiskGb << 30;
        if (disk is null) throw ProxmoxCommands.Failure();
        if (requested < disk) throw new ChildValidationException("validation", "diskGb");
        var description = ProxmoxCommands.String(config, "description")!;
        var oldTemplate = description.Split(' ').FirstOrDefault(p => p.StartsWith("template=", StringComparison.Ordinal))?[9..] ?? "none";
        var template = resendTemplate ? Template(hardware.SecureBootTemplate) : oldTemplate;
        var oldSecure = ProxmoxCommands.Property(ProxmoxCommands.String(config, "efidisk0"), "pre-enrolled-keys") == "1";
        var replaceEfi = !config.TryGetProperty("efidisk0", out _) || oldSecure != hardware.SecureBoot || template != oldTemplate;
        // Journal the old EFI/TPM disks before any command can detach them.
        owner = owner with { Volumes = owner.Volumes.Concat(OwnedVolumes(config, id)).Distinct().ToArray() };
        WriteOwnership(owner);
        var number = ProxmoxCommands.Number(id);
        if (replaceEfi && config.TryGetProperty("efidisk0", out _))
            await commands.QmAsync(["set", number, "--delete", "efidisk0", "--force", "1"], ct);
        if (!hardware.Tpm && config.TryGetProperty("tpmstate0", out _))
            await commands.QmAsync(["set", number, "--delete", "tpmstate0", "--force", "1"], ct);
        var args = new List<string> { "set", number, "--cores", ProxmoxCommands.Number(hardware.Cpus),
            "--sockets", "1", "--memory", ProxmoxCommands.Number(hardware.RamMb), "--balloon", "0" };
        if (replaceEfi) args.AddRange(["--efidisk0", Efi(hardware.SecureBoot)]);
        if (resendTemplate) args.AddRange(["--description", Description(owner, template)]);
        if (hardware.Tpm && !config.TryGetProperty("tpmstate0", out _)) args.AddRange(["--tpmstate0", Storage + ":1,version=v2.0"]);
        args.AddRange(["--boot", "order=" + Boot(hardware.BootOrder, HasMedia(config, "ide2"), HasMedia(config, "ide0"), hardware.NetworkAttached, slot)]);
        await commands.QmAsync(args, ct);
        if (requested > disk) await commands.QmAsync(["disk", "resize", number, slot, ProxmoxCommands.Number(hardware.DiskGb) + "G"], ct);
        var actual = await commands.ConfigAsync(id, ct);
        VerifyOwner(actual, owner);
        WriteOwnership(owner with { Volumes = owner.Volumes.Concat(OwnedVolumes(actual, id)).Distinct().ToArray() });
        if (Number(actual, "cores") != hardware.Cpus || Number(actual, "sockets", 1) != 1 ||
            Number(actual, "memory") != hardware.RamMb || Number(actual, "balloon", 0) != 0 ||
            DiskBytes(ProxmoxCommands.String(actual, slot)) != requested ||
            (ProxmoxCommands.Property(ProxmoxCommands.String(actual, "efidisk0"), "pre-enrolled-keys") == "1") != hardware.SecureBoot ||
            actual.TryGetProperty("tpmstate0", out _) != hardware.Tpm ||
            ProxmoxCommands.Property(ProxmoxCommands.String(actual, "boot"), "order") != args[^1]![6..] ||
            resendTemplate && !(ProxmoxCommands.String(actual, "description") ?? "").Split(' ').Contains("template=" + template))
            throw ProxmoxCommands.Failure();
        if (await commands.StateAsync(id, ct) != VmState.Off) throw new ChildValidationException("vm-not-off", "vm");
    }

    private static int Number(JsonElement config, string key, int fallback = -1) => config.TryGetProperty(key, out var value) &&
        int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static long? DiskBytes(string? value)
    {
        var size = ProxmoxCommands.Property(value, "size");
        if (string.IsNullOrEmpty(size)) return null;
        var factor = size[^1] switch { 'T' => 1L << 40, 'G' => 1L << 30, 'M' => 1L << 20, 'K' => 1L << 10, _ => 1L };
        return decimal.TryParse(factor == 1 ? size : size[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) &&
            number >= 0 && number <= long.MaxValue / factor ? (long)(number * factor) : null;
    }
    private static bool HasMedia(JsonElement config, string slot) => ProxmoxCommands.String(config, slot) is { } text &&
        text.Split(',').Contains("media=cdrom", StringComparer.Ordinal) && text.Split(',')[0] is not ("none" or "cdrom");

    public async Task SetMediaAsync(string name, string? installMediaPath, string? auxiliaryMediaPath, IReadOnlyList<BootDevice> bootOrder, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        var install = installMediaPath is null ? null : media.ToVolume(installMediaPath);
        var auxiliary = auxiliaryMediaPath is null ? null : media.ToVolume(auxiliaryMediaPath);
        _ = Boot(bootOrder, install is not null, auxiliary is not null, true, "scsi0");
        var (id, config, owner) = await OffChildAsync(name, ct);
        var args = new List<string> { "set", ProxmoxCommands.Number(id) };
        var delete = new List<string>();
        foreach (var (slot, volume) in new[] { ("ide2", install), ("ide0", auxiliary) })
        {
            if (config.TryGetProperty(slot, out _) && !(ProxmoxCommands.String(config, slot) ?? "").Split(',').Contains("media=cdrom"))
                throw new ChildValidationException("artifact-ownership-unverified", "media");
            if (volume is not null) args.AddRange(["--" + slot, volume + ",media=cdrom"]);
            else if (config.TryGetProperty(slot, out _)) delete.Add(slot);
        }
        if (delete.Count > 0) args.AddRange(["--delete", string.Join(',', delete)]);
        var order = Boot(bootOrder, install is not null, auxiliary is not null, config.TryGetProperty("net0", out _), ProxmoxChildVmPlatform.DiskSlot(config));
        args.AddRange(["--boot", "order=" + order]);
        await commands.QmAsync(args, ct);
        var actual = await commands.ConfigAsync(id, ct); VerifyOwner(actual, owner);
        var attached = Attached(actual);
        if (!attached.Complete || attached.InstallPath != installMediaPath || attached.AuxiliaryPath != auxiliaryMediaPath ||
            ProxmoxCommands.Property(ProxmoxCommands.String(actual, "boot"), "order") != order) throw ProxmoxCommands.Failure();
        if (await commands.StateAsync(id, ct) != VmState.Off) throw new ChildValidationException("vm-not-off", "vm");
    }
    public async Task<AttachedMedia> GetAttachedMediaAsync(string name, CancellationToken ct) =>
        Attached(await commands.ConfigAsync(await commands.RequireAsync(name, ct), ct));
    private AttachedMedia Attached(JsonElement config)
    {
        var complete = true;
        string? PathFor(string slot)
        {
            var value = ProxmoxCommands.String(config, slot);
            if (value is null) return null;
            if (!value.Split(',').Contains("media=cdrom", StringComparer.Ordinal)) { complete = false; return null; }
            var volume = value.Split(',')[0];
            if (volume == "none") return null;
            try { return media.FromVolume(volume); }
            catch (ChildValidationException) { complete = false; return null; }
        }
        var install = PathFor("ide2"); var auxiliary = PathFor("ide0");
        return new(install, auxiliary, complete);
    }

    public async Task<GracefulShutdownOutcome> ShutdownGracefulAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentGuard.VmName(name);
        if (timeout.TotalSeconds < 1 || timeout.TotalSeconds > 86400) throw new ArgumentOutOfRangeException(nameof(timeout));
        var id = await commands.RequireAsync(name, ct);
        if (await commands.StateAsync(id, ct) == VmState.Off) return GracefulShutdownOutcome.Completed;
        var config = await commands.ConfigAsync(id, ct);
        if (Number(config, "acpi", 1) == 0 && !AgentEnabled(config)) return GracefulShutdownOutcome.Unavailable;
        progress?.Report("Requesting graceful guest shutdown.");
        ProcessResult result;
        try { result = await commands.RunAsync(options.Proxmox.QmPath,
            ["shutdown", ProxmoxCommands.Number(id), "--timeout", ProxmoxCommands.Number((int)Math.Ceiling(timeout.TotalSeconds))],
            ct, timeout + TimeSpan.FromMinutes(1)); }
        catch (ProxmoxOperationException) { return GracefulShutdownOutcome.Failed; }
        if (result.TimedOut) return GracefulShutdownOutcome.Timeout;
        var error = result.StandardError.ToLowerInvariant();
        if (!result.Succeeded)
        {
            if (error.Contains("timeout") || error.Contains("timed out")) return GracefulShutdownOutcome.Timeout;
            if (error.Contains("acpi") && error.Contains("disabled") || error.Contains("guest agent is not running")) return GracefulShutdownOutcome.Unavailable;
            return GracefulShutdownOutcome.Failed;
        }
        return await commands.StateAsync(id, ct) == VmState.Off ? GracefulShutdownOutcome.Completed : GracefulShutdownOutcome.Timeout;
    }
    private static bool AgentEnabled(JsonElement config) => config.TryGetProperty("agent", out var value) &&
        (value.ToString() == "1" || ProxmoxCommands.Property(value.ToString(), "enabled") == "1");
    public async Task<VmCapabilitiesSnapshot> GetVmCapabilitiesAsync(string name, CancellationToken ct)
    {
        var id = await commands.RequireAsync(name, ct);
        var config = await commands.ConfigAsync(id, ct);
        var state = await commands.StateAsync(id, ct);
        var vga = ProxmoxCommands.String(config, "vga") ?? "std";
        var video = vga != "none" && !vga.StartsWith("serial", StringComparison.Ordinal);
        var tablet = Number(config, "tablet", 1) != 0;
        int? width = null, height = null;
        if (video && state is VmState.Running or VmState.Paused)
        {
            try
            {
                var screen = await new ProxmoxConsoleTransport(processes, options).GetScreenAsync(name, ct);
                width = screen.NativeWidth; height = screen.NativeHeight;
            }
            catch (ConsoleTransportException) { /* Device capabilities remain readable when a capture fails. */ }
        }
        return new(name, state, video, true, tablet, true, width, height, false,
            ProxmoxCommands.String(config, "bios") == "ovmf" ? 2 : 1,
            Number(config, "acpi", 1) != 0 || AgentEnabled(config) ? Conditional : Unsupported,
            new(Supported, ProxmoxCommands.HasChildTag(config) ? Unsupported : Supported, Unsupported));
    }
}
