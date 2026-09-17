using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Windows.Internal;

namespace Constructd.Proxmox;

public sealed partial class ProxmoxChildVmPlatform(IProcessRunner processes, IHypervisorDriver driver,
    ConstructdOptions options, IVmRepository vms, IClock clock)
    : IChildVmDriver, IChildVmStorage, IChildVmCreationOwnership
{
    private readonly ProxmoxCommands commands = new(processes, options);
    private readonly ProxmoxMediaVolumes media = new(options);
    private const CapabilityLevel Supported = CapabilityLevel.Supported;
    private const CapabilityLevel Unsupported = CapabilityLevel.Unsupported;
    private const CapabilityLevel Conditional = CapabilityLevel.Conditional;

    public Task<BackendCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new BackendCapabilities("proxmox", driver.Capabilities, [2], 2, Supported,
            [SecureBootTemplate.MicrosoftWindows, SecureBootTemplate.MicrosoftUefiCertificateAuthority], Supported,
            false, 2, Supported, Supported, UnsupportedCapabilities.Console,
            new(Supported, Supported, Unsupported, Conditional, Unsupported, Unsupported),
            Unsupported, Unsupported, driver.Capabilities.Suspend ? Supported : Unsupported, Conditional,
            ["Both Secure Boot templates use OVMF's bundled Microsoft keys. Guest agent addresses are unverified.",
             "Console text uses US-layout ASCII; unsupported keys and scancodes are refused.",
             "Browser consoles use session-bound VNC through the trusted Construct gateway. The primary-to-node VNC hop is unencrypted."]));
    }

    public Task<ChildStoragePlacement> ResolvePrimaryStorageAsync(string name, CancellationToken ct) => ResolveStorageAsync(name, ct);
    public Task<ChildStoragePlacement> ResolveStorageAsync(string name, CancellationToken ct)
    {
        ArgumentGuard.VmName(name); ct.ThrowIfCancellationRequested();
        var storage = Storage;
        return Task.FromResult(new ChildStoragePlacement($"{storage}:vm-{name}-disk-0", storage, storage));
    }
    private string Storage => Identifier(options.Proxmox.Storage);
    private static string Identifier(string value) => Regex.IsMatch(value, @"\A[a-zA-Z][a-zA-Z0-9_.-]{0,63}\z")
        ? value : throw new ChildValidationException("validation", "proxmoxConfiguration");
    private static string Template(SecureBootTemplate? template) => template switch
    {
        SecureBootTemplate.MicrosoftWindows => "microsoftWindows",
        SecureBootTemplate.MicrosoftUefiCertificateAuthority => "microsoftUefiCertificateAuthority",
        _ => "none"
    };

    public Task CreateAsync(ChildVmDescriptor descriptor, IProgress<string>? progress, CancellationToken ct) =>
        CreateOwnedAsync(descriptor, Guid.NewGuid().ToString("n"), progress, ct);

    public async Task CreateOwnedAsync(ChildVmDescriptor descriptor, string operationId, IProgress<string>? progress, CancellationToken ct)
    {
        var name = ArgumentGuard.VmName(descriptor.Name);
        if (!OperationFingerprint.ValidKey(operationId)) throw new ChildValidationException("validation", "operationId");
        var h = descriptor.Hardware;
        HardwarePresets.ValidateCapabilities(h, await GetCapabilitiesAsync(ct), descriptor.AuxiliaryMediaPath is not null);
        var placement = await ResolveStorageAsync(name, ct);
        if (descriptor.VhdPath is not null && descriptor.VhdPath != placement.DiskPath)
            throw new ChildValidationException("storage-placement-unavailable", "diskPath");
        var install = descriptor.InstallMediaPath is null ? null : media.ToVolume(descriptor.InstallMediaPath);
        var auxiliary = descriptor.AuxiliaryMediaPath is null ? null : media.ToVolume(descriptor.AuxiliaryMediaPath);
        var bridge = Identifier(options.Proxmox.Bridge);
        if (await commands.FindAsync(name, ct) is not null || ReadOwnership(name) is not null)
            throw new ChildValidationException("name-taken", "name");
        var next = await commands.QueryAsync(["get", "/cluster/nextid"], ct);
        if (!int.TryParse(next.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            throw ProxmoxCommands.Failure();
        var parent = (await vms.GetAsync(name, ct))?.Parent ?? "none";
        if (parent != "none") ArgumentGuard.VmName(parent);
        var owner = new ChildOwnership(name, id, Guid.NewGuid().ToString(), operationId, parent, clock.UtcNow, []);
        WriteOwnership(owner, create: true);
        progress?.Report("Creating child VM hardware and disk.");
        var args = new List<string> { "create", ProxmoxCommands.Number(id), "--name", name,
            "--ostype", h.SecureBootTemplate == SecureBootTemplate.MicrosoftWindows ? "win11" : "l26",
            "--machine", "q35", "--bios", "ovmf", "--efidisk0", Efi(h.SecureBoot),
            // Same CPU model as a primary without nesting. Without --cpu, qm falls back to kvm64,
            // which hides POPCNT and SSE4.2; Windows 11 since 24H2 refuses to boot on that and
            // loops in the boot manager (seen on the first Windows child field test).
            "--cpu", ArgumentGuard.Text(options.Proxmox.CpuTypeWithoutNesting, "Constructd:Proxmox:CpuTypeWithoutNesting", 64),
            "--scsihw", "virtio-scsi-single",
            // Windows has no in-box virtio driver: Setup shows no disk on virtio-scsi. The Windows
            // preset therefore gets a SATA (AHCI) disk, which Setup sees without a driver ISO.
            "--" + DiskSlot(h), $"{Storage}:{h.DiskGb},discard=on",
            "--agent", "enabled=1", "--memory", ProxmoxCommands.Number(h.RamMb), "--balloon", "0",
            "--cores", ProxmoxCommands.Number(h.Cpus), "--sockets", "1", "--tablet", "1",
            "--tags", "construct-child", "--smbios1", "uuid=" + owner.Incarnation,
            "--description", Description(owner, Template(h.SecureBootTemplate)) };
        if (h.Tpm) args.AddRange(["--tpmstate0", Storage + ":1,version=v2.0"]);
        if (install is not null) args.AddRange(["--ide2", install + ",media=cdrom"]);
        if (auxiliary is not null) args.AddRange(["--ide0", auxiliary + ",media=cdrom"]);
        if (h.NetworkAttached) args.AddRange(["--net0", "virtio,bridge=" + bridge]);
        var boot = Boot(h.BootOrder, install is not null, auxiliary is not null, h.NetworkAttached, DiskSlot(h));
        if (boot.Length > 0) args.AddRange(["--boot", "order=" + boot]);
        await commands.QmAsync(args, ct, TimeSpan.FromMinutes(30));
        var config = await commands.ConfigAsync(id, ct);
        VerifyOwner(config, owner);
        WriteOwnership(owner with { Volumes = OwnedVolumes(config, id) });
    }

    private string Efi(bool secure) => Storage + ":1,efitype=4m,pre-enrolled-keys=" + (secure ? "1" : "0");
    private static string Description(ChildOwnership owner, string template) =>
        $"construct-child parent={owner.Parent} template={template} created={owner.Created:O} operation={owner.OperationId} uuid={owner.Incarnation}";
    /// <summary>The system disk slot: SATA for the Windows preset (in-box driver), virtio-scsi otherwise.</summary>
    public static string DiskSlot(ChildHardware h) => h.SecureBootTemplate == SecureBootTemplate.MicrosoftWindows ? "sata0" : "scsi0";
    /// <summary>The slot a created VM actually uses, from its config (either slot may be present).</summary>
    public static string DiskSlot(JsonElement config) => config.TryGetProperty("sata0", out _) ? "sata0" : "scsi0";
    private static string Boot(IReadOnlyList<BootDevice> order, bool install, bool auxiliary, bool network, string disk)
    {
        if (order.Any(d => !Enum.IsDefined(d)) || order.Distinct().Count() != order.Count)
            throw new ChildValidationException("validation", "bootOrder");
        return string.Join(';', order.Select(d => d switch
        {
            BootDevice.InstallMedia when install => "ide2", BootDevice.AuxiliaryMedia when auxiliary => "ide0",
            BootDevice.Disk => disk, BootDevice.Network when network => "net0", _ => null
        }).OfType<string>());
    }

    public async Task<string?> GetVmIdAsync(string name, CancellationToken ct)
    {
        var id = await commands.FindAsync(name, ct);
        return id is null ? null : ProxmoxCommands.Incarnation(await commands.ConfigAsync(id.Value, ct)) ?? throw ProxmoxCommands.Failure();
    }

}
