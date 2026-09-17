using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Proxmox;

/// <summary>
/// A driver operation did not succeed. Like the Hyper-V driver's exception it names only the
/// operation and the VM — never a command line or the child's output — because it is what reaches
/// the job error, the audit trail and the API response (<see cref="SafeError"/>).
/// </summary>
public sealed class ProxmoxOperationException(string operation, string vmName, string? detail = null)
    : Exception(
        vmName.Length == 0
            ? $"The Proxmox driver failed during '{operation}'."
            : $"The Proxmox driver failed during '{operation}' for VM '{vmName}'."), IConstructdError
{
    public string Operation { get; } = operation;

    public string VmName { get; } = vmName;

    /// <summary>How it failed, in this service's own words (an exit code, a timeout, a missing seed).</summary>
    public string? Detail { get; } = detail;
}

/// <summary>
/// <see cref="IHypervisorDriver"/> for a Proxmox VE node the service runs on. Every operation is one
/// <c>qm</c> or <c>pvesh</c> invocation through <see cref="IProcessRunner"/> (argv, never a shell), so
/// the tests pin the exact argument vectors on a machine that has neither program.
///
/// A VM is a clone of the cached Ubuntu cloud image (<c>Constructd:Proxmox:ImageVolume</c>) with a
/// Proxmox cloud-init drive. The per-VM seed — hostname, seed user, bootstrap key — is the snippet
/// <see cref="CloudInitSeedBuilder"/> wrote, referenced through <see cref="VmDescriptor.IsoPath"/>
/// (the contract's "install media" slot). There is no unattended install: the guest is up in about a
/// minute, the QEMU guest agent the seed installs reports its DHCP address, and the client provisions
/// it over SSH exactly as it provisions a Hyper-V guest.
///
/// VMs are addressed by NAME on the contract side and by numeric id on the Proxmox side; the id is
/// looked up in the cluster resource list on every call rather than cached, so a VM recreated behind
/// the service's back is never driven under a stale id.
/// </summary>
public sealed class ProxmoxDriver : IHypervisorDriver, IVmCpuDriver, IVmMemoryDriver, IGuestNetworkConfigurator, IVmNestedDriver
{
    /// <summary>Cloning the image and starting the VM; well short of leaving a hung <c>qm</c> forever.</summary>
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A power change (a graceful shutdown waits up to two minutes) or a configuration write.</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMinutes(5);

    /// <summary>A read-only <c>pvesh get</c>.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Graceful shutdown budget handed to <c>qm shutdown</c> before it powers the VM off.</summary>
    private const int ShutdownTimeoutSeconds = 120;

    private readonly IProcessRunner _processes;
    private readonly ConstructdOptions _options;
    private readonly IProxmoxSeedFiles _seeds;
    private readonly ILogger<ProxmoxDriver> _logger;

    public ProxmoxDriver(
        IProcessRunner processes,
        ConstructdOptions options,
        IProxmoxSeedFiles seeds,
        ILogger<ProxmoxDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(logger);

        _processes = processes;
        _options = options;
        _seeds = seeds;
        _logger = logger;
    }

    /// <summary>
    /// No checkpoint policy (Proxmox has snapshots but no "checkpoint at every start"), suspend to disk
    /// yes (<c>qm suspend --todisk</c>; a start resumes it), and no desktop console: the node's own
    /// noVNC needs a Proxmox login, which the service does not hand out.
    /// </summary>
    public DriverCapabilities Capabilities { get; } =
        new(Checkpoints: false, Suspend: true, Console: DriverConsole.None);

    /// <summary>The node this driver addresses: the configured one, or the machine it runs on.</summary>
    public string Node => ResolveNode(_options);

    public bool NestedAvailable => ProxmoxNestedCapability.IsAvailable();

    private string CpuType(bool nested) => ArgumentGuard.Text(
        nested ? _options.Proxmox.CpuType : _options.Proxmox.CpuTypeWithoutNesting,
        nested ? "Constructd:Proxmox:CpuType" : "Constructd:Proxmox:CpuTypeWithoutNesting", 64);

    public async Task<bool> GetNestedAsync(string name, CancellationToken ct)
    {
        var (vmName, _, vmId) = await RequireVmWithIdAsync("get-nested", name, ct);
        var config = await PveshAsync("get-nested", vmName, ["get", VmPath(vmId) + "/config"], ct);
        return string.Equals(ReadString(config, "cpu")?.Split(',')[0], CpuType(true), StringComparison.Ordinal);
    }

    public async Task SetNestedAsync(string name, bool enabled, CancellationToken ct)
    {
        var (vmName, id) = await RequireVmAsync("set-nested", name, ct);
        await RunQmAsync("set-nested", vmName, ["set", id, "--cpu", CpuType(enabled)], ShortTimeout, null, ct);
    }

    /// <summary><c>Constructd:Proxmox:Node</c>, or this machine's short host name when unset.</summary>
    public static string ResolveNode(ConstructdOptions options) =>
        string.IsNullOrWhiteSpace(options.Proxmox.Node)
            ? Environment.MachineName.Split('.')[0]
            : options.Proxmox.Node.Trim();

    public async Task CreateVmAsync(
        VmDescriptor descriptor,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var name = ArgumentGuard.VmName(descriptor.Name);
        var cpu = ArgumentGuard.Positive(descriptor.Cpu, "cpu", 64);
        var ramGb = ArgumentGuard.Positive(descriptor.RamGb, "ram", 1024);
        var diskGb = ArgumentGuard.Positive(descriptor.DiskGb, "disk", 8192);
        var seed = SeedVolume(descriptor.IsoPath, name);
        var storage = ArgumentGuard.Text(_options.Proxmox.Storage, "Constructd:Proxmox:Storage", 64);
        var image = ArgumentGuard.Text(_options.Proxmox.ImageVolume, "Constructd:Proxmox:ImageVolume", 256);
        var bridge = ArgumentGuard.Text(_options.Proxmox.Bridge, "Constructd:Proxmox:Bridge", 32);
        var cpuType = CpuType(descriptor.Nested);

        if (await TryResolveVmIdAsync(name, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw Fail("create-vm", name, "a VM with this name already exists on the node");
        }

        var vmId = await NextVmIdAsync(name, cancellationToken).ConfigureAwait(false);
        var id = ArgumentGuard.Invariant(vmId);

        progress?.Report(
            $"creating VM {id} '{name}' on node {Node}: {cpu} vCPU, {ramGb} GB RAM, {diskGb} GB disk, cloned from {image}");

        await RunQmAsync("create-vm", name,
        [
            "create", id,
            "--name", name,
            "--cores", ArgumentGuard.Invariant(cpu),
            "--sockets", "1",
            "--cpu", cpuType,
            "--memory", ArgumentGuard.Invariant(ramGb * 1024),
            "--ostype", "l26",
            "--scsihw", "virtio-scsi-single",
            "--scsi0", $"{storage}:0,import-from={image},discard=on",
            "--ide2", $"{storage}:cloudinit",
            "--net0", $"virtio,bridge={bridge}",
            "--boot", "order=scsi0",
            "--agent", "enabled=1",
            "--serial0", "socket",
            "--ipconfig0", "ip=dhcp",
            "--cicustom", $"user={seed}",
            "--tags", "construct",
        ], CreateTimeout, progress, cancellationToken).ConfigureAwait(false);

        // The image is a few GB; the descriptor's disk is the cap the guest grows into (cloud-init's
        // growpart expands the root file system on first boot).
        await RunQmAsync("resize-disk", name,
            ["disk", "resize", id, "scsi0", $"{ArgumentGuard.Invariant(diskGb)}G"],
            ShortTimeout, progress, cancellationToken).ConfigureAwait(false);

        await RunQmAsync("start-vm", name, ["start", id], ShortTimeout, progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report($"VM {id} started; cloud-init is seeding '{name}'");
    }

    public async Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        name = ArgumentGuard.VmName(name);

        var vmId = await TryResolveVmIdAsync(name, cancellationToken).ConfigureAwait(false);
        if (vmId is null)
        {
            progress?.Report($"no VM named '{name}' on node {Node}; nothing to remove");
            _seeds.Remove(name);
            return;
        }

        var id = ArgumentGuard.Invariant(vmId.Value);
        var state = await ReadStateAsync(name, vmId.Value, cancellationToken).ConfigureAwait(false);
        if (state is VmState.Running or VmState.Paused)
        {
            progress?.Report($"powering VM {id} off");
            await RunQmAsync("stop-vm", name, ["stop", id], ShortTimeout, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report($"destroying VM {id} and its disks");
        await RunQmAsync("remove-vm", name,
            ["destroy", id, "--purge", "1", "--destroy-unreferenced-disks", "1", "--skiplock", "1"],
            RemoveTimeout, progress, cancellationToken).ConfigureAwait(false);

        _seeds.Remove(name);
        progress?.Report($"VM {id} '{name}' removed");
    }

    public async Task StartAsync(string name, CancellationToken cancellationToken)
    {
        var (vmName, id) = await RequireVmAsync("start-vm", name, cancellationToken).ConfigureAwait(false);
        await RunQmAsync("start-vm", vmName, ["start", id], ShortTimeout, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A graceful shutdown that powers the VM off when the guest does not finish within
    /// <see cref="ShutdownTimeoutSeconds"/>. A VM that is already off (or suspended to disk, which
    /// Proxmox reports as stopped) needs nothing.
    /// </summary>
    public async Task StopAsync(string name, CancellationToken cancellationToken)
    {
        var (vmName, id, vmId) = await RequireVmWithIdAsync("stop-vm", name, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(vmName, vmId, cancellationToken).ConfigureAwait(false);
        if (state is VmState.Off or VmState.Saved)
        {
            return;
        }

        await RunQmAsync("stop-vm", vmName,
            ["shutdown", id, "--timeout", ArgumentGuard.Invariant(ShutdownTimeoutSeconds), "--forceStop", "1"],
            ShortTimeout, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(string name, CancellationToken cancellationToken)
    {
        var (vmName, id) = await RequireVmAsync("save-vm", name, cancellationToken).ConfigureAwait(false);
        await RunQmAsync("save-vm", vmName, ["suspend", id, "--todisk", "1"], ShortTimeout, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>absent</c> only when the cluster resource list was read and holds no VM of that name;
    /// a failed read is <see cref="VmState.Unknown"/> — "can't tell", never "not installed".
    /// </summary>
    public async Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken)
    {
        name = ArgumentGuard.VmName(name);
        try
        {
            var vmId = await TryResolveVmIdAsync(name, cancellationToken).ConfigureAwait(false);
            if (vmId is null)
            {
                return VmState.Absent;
            }

            return await ReadStateAsync(name, vmId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (ProxmoxOperationException)
        {
            return VmState.Unknown;
        }
    }

    /// <summary>
    /// The contract's state, from <c>status/current</c>: a suspend-to-disk lock is <c>saved</c>,
    /// a running VM whose QMP status is paused is <c>paused</c>, otherwise running/stopped map
    /// straight across and anything else is <see cref="VmState.Unknown"/>.
    /// </summary>
    public static VmState MapState(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object)
        {
            return VmState.Unknown;
        }

        var lockText = ReadString(status, "lock");
        if (lockText is "suspended" or "suspending")
        {
            return VmState.Saved;
        }

        return ReadString(status, "status") switch
        {
            "running" => ReadString(status, "qmpstatus") == "paused" ? VmState.Paused : VmState.Running,
            "stopped" => VmState.Off,
            _ => VmState.Unknown,
        };
    }

    /// <summary>
    /// The guest's first non-loopback IPv4 address as the QEMU guest agent reports it, on SSH port
    /// 22 — or <c>null</c> while the agent is not answering yet (first boot) or the VM is absent.
    /// </summary>
    public async Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken)
    {
        name = ArgumentGuard.VmName(name);
        var vmId = await TryResolveVmIdAsync(name, cancellationToken).ConfigureAwait(false);
        if (vmId is null)
        {
            return null;
        }

        JsonElement interfaces;
        try
        {
            interfaces = await PveshAsync("get-endpoint", name,
                ["get", VmPath(vmId.Value) + "/agent/network-get-interfaces"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProxmoxOperationException)
        {
            // The agent is not running (yet); the caller polls.
            return null;
        }

        var address = ParseGuestAddress(interfaces);
        return address is null ? null : new Endpoint(address, 22);
    }

    /// <summary>
    /// The first IPv4 address of a real guest interface out of the agent's
    /// <c>network-get-interfaces</c> answer. Loopback and the guest's own container/bridge devices
    /// (Docker, veth, virbr) are skipped: they are not where SSH listens for the outside.
    /// </summary>
    public static string? ParseGuestAddress(JsonElement interfaces)
    {
        if (interfaces.ValueKind == JsonValueKind.Object && interfaces.TryGetProperty("result", out var result))
        {
            interfaces = result;
        }

        if (interfaces.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var device in interfaces.EnumerateArray())
        {
            var deviceName = ReadString(device, "name") ?? string.Empty;
            if (deviceName == "lo" || deviceName.StartsWith("docker", StringComparison.Ordinal) ||
                deviceName.StartsWith("br-", StringComparison.Ordinal) ||
                deviceName.StartsWith("veth", StringComparison.Ordinal) ||
                deviceName.StartsWith("virbr", StringComparison.Ordinal))
            {
                continue;
            }

            if (!device.TryGetProperty("ip-addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in addresses.EnumerateArray())
            {
                if (ReadString(entry, "ip-address-type") != "ipv4")
                {
                    continue;
                }

                var text = ReadString(entry, "ip-address");
                if (text is not null && IPAddress.TryParse(text, out var ip) &&
                    ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) &&
                    !text.StartsWith("169.254.", StringComparison.Ordinal))
                {
                    return text;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Polls the guest agent for an address and then SSH on it, until <paramref name="timeout"/>.
    /// Returns false on expiry — non-fatal by contract; the job decides.
    /// </summary>
    public async Task<bool> WaitReachableAsync(
        string name,
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        name = ArgumentGuard.VmName(name);
        var deadline = DateTimeOffset.UtcNow + timeout;
        var lastNote = DateTimeOffset.MinValue;
        progress?.Report($"waiting for '{name}' to report an address and answer on SSH");

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Endpoint? endpoint = null;
            try
            {
                endpoint = await GetEndpointAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (ProxmoxOperationException)
            {
                // A transient pvesh failure is just "not yet".
            }

            if (endpoint is not null &&
                await ProbeSshAsync(endpoint.SshHost, endpoint.SshPort, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report($"ssh answers at {endpoint.SshHost}:{endpoint.SshPort}");
                return true;
            }

            if (DateTimeOffset.UtcNow - lastNote > TimeSpan.FromSeconds(30))
            {
                lastNote = DateTimeOffset.UtcNow;
                progress?.Report(endpoint is null
                    ? "guest agent has not reported an address yet"
                    : $"guest at {endpoint.SshHost}, ssh not answering yet");
            }

            await Task.Delay(ProbeInterval, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report($"'{name}' did not become reachable within {timeout.TotalMinutes:0} minutes");
        return false;
    }

    /// <summary>
    /// Nothing to eject: the cloud-init drive is not install media, and cloud-init only acts on it
    /// once per instance id. Kept because the job calls it unconditionally after every create.
    /// </summary>
    public Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentGuard.VmName(name);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task SetCpuCountAsync(string name, int cpus, CancellationToken cancellationToken)
    {
        var count = ArgumentGuard.Positive(cpus, "cpu", 64);
        var (vmName, id) = await RequireVmAsync("set-cpu", name, cancellationToken).ConfigureAwait(false);
        await RunQmAsync("set-cpu", vmName, ["set", id, "--cores", ArgumentGuard.Invariant(count)],
            ShortTimeout, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMemoryAsync(string name, int ramGb, CancellationToken cancellationToken)
    {
        var gb = ArgumentGuard.Positive(ramGb, "ram", 1024);
        var (vmName, id) = await RequireVmAsync("set-memory", name, cancellationToken).ConfigureAwait(false);
        await RunQmAsync("set-memory", vmName, ["set", id, "--memory", ArgumentGuard.Invariant(gb * 1024)],
            ShortTimeout, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task ConfigureNetworkAsync(string name, string? address, string? gateway, IReadOnlyList<string>? dns, CancellationToken ct)
    {
        var ipconfig = "ip=dhcp";
        if (address is not null)
        {
            var parts = address.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 32)
                throw new ArgumentException("Expected an IPv4 CIDR.", nameof(address));
            ipconfig = $"ip={ArgumentGuard.IPv4(parts[0], "address")}/{prefix},gw={ArgumentGuard.IPv4(gateway!, "gateway")}";
        }
        else if (gateway is not null) throw new ArgumentException("A gateway requires a fixed address.", nameof(gateway));
        var resolvers = dns?.Select(d => ArgumentGuard.IPv4(d, "dns")).ToArray() ?? [];
        var (vmName, id, vmId) = await RequireVmWithIdAsync("set-network", name, ct);
        if (await ReadStateAsync(vmName, vmId, ct) != VmState.Off)
            throw Fail("set-network", vmName, "the VM must be fully stopped");
        await RunQmAsync("set-network", vmName,
            ["set", id, "--ipconfig0", ipconfig, .. resolvers.Length > 0
                ? new[] { "--nameserver", string.Join(" ", resolvers) } : new[] { "--delete", "nameserver" }],
            ShortTimeout, null, ct);
        // Generate the changed cloud-init drive before the next cold boot.
        await RunQmAsync("set-network", vmName, ["cloudinit", "update", id], ShortTimeout, null, ct);
    }

    // ── Lookups ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The numeric id of the QEMU VM called <paramref name="name"/> on this node, or null when the
    /// (successfully read) resource list has none. Throws when the list cannot be read.
    /// </summary>
    public async Task<int?> TryResolveVmIdAsync(string name, CancellationToken cancellationToken)
    {
        var resources = await PveshAsync("resolve-vm", name,
            ["get", "/cluster/resources", "--type", "vm"], cancellationToken).ConfigureAwait(false);

        if (resources.ValueKind != JsonValueKind.Array)
        {
            throw Fail("resolve-vm", name, "the cluster resource list was not the expected JSON array");
        }

        foreach (var entry in resources.EnumerateArray())
        {
            if (ReadString(entry, "type") != "qemu" ||
                !string.Equals(ReadString(entry, "name"), name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadString(entry, "node"), Node, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.TryGetProperty("vmid", out var vmid) && vmid.TryGetInt32(out var id))
            {
                return id;
            }
        }

        return null;
    }

    private async Task<(string Name, string Id)> RequireVmAsync(string operation, string name, CancellationToken cancellationToken)
    {
        var (vmName, id, _) = await RequireVmWithIdAsync(operation, name, cancellationToken).ConfigureAwait(false);
        return (vmName, id);
    }

    private async Task<(string Name, string Id, int VmId)> RequireVmWithIdAsync(string operation, string name, CancellationToken cancellationToken)
    {
        name = ArgumentGuard.VmName(name);
        var vmId = await TryResolveVmIdAsync(name, cancellationToken).ConfigureAwait(false)
                   ?? throw Fail(operation, name, "no VM of that name exists on the node");
        return (name, ArgumentGuard.Invariant(vmId), vmId);
    }

    private async Task<int> NextVmIdAsync(string name, CancellationToken cancellationToken)
    {
        var next = await PveshAsync("next-id", name, ["get", "/cluster/nextid"], cancellationToken).ConfigureAwait(false);
        var text = next.ValueKind switch
        {
            JsonValueKind.Number => next.GetRawText(),
            JsonValueKind.String => next.GetString(),
            _ => null,
        };

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : throw Fail("next-id", name, "the node did not answer with a usable next VM id");
    }

    private async Task<VmState> ReadStateAsync(string name, int vmId, CancellationToken cancellationToken)
    {
        var status = await PveshAsync("get-state", name, ["get", VmPath(vmId) + "/status/current"], cancellationToken)
            .ConfigureAwait(false);
        return MapState(status);
    }

    private string VmPath(int vmId) => $"/nodes/{Node}/qemu/{ArgumentGuard.Invariant(vmId)}";

    /// <summary>The seed the ISO-builder slot carries: a snippet volume id, and nothing else.</summary>
    private string SeedVolume(string? isoPath, string name)
    {
        if (string.IsNullOrWhiteSpace(isoPath))
        {
            throw Fail("create-vm", name, "no cloud-init seed volume was supplied for the VM");
        }

        var seed = isoPath.Trim();
        if (!CloudInitSeedBuilder.IsSnippetVolume(seed))
        {
            throw Fail("create-vm", name, "the supplied seed is not a Proxmox snippet volume id");
        }

        return seed;
    }

    private static async Task<bool> ProbeSshAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ProbeTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── Processes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>qm</c> invocation. Its stdout is streamed to the job as progress (the image import
    /// prints its transfer progress there); on failure the first stderr lines follow it — the
    /// job's progress log is the documented channel for a dependency's own words, the exception
    /// is not (<see cref="SafeError"/>).
    /// </summary>
    private async Task RunQmAsync(
        string operation,
        string vmName,
        string[] arguments,
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync(
            _options.Proxmox.QmPath, arguments, standardInput: null, timeout, progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw Fail(operation, vmName, $"qm timed out after {timeout.TotalMinutes:0} minutes");
        }

        if (result.ExitCode != 0)
        {
            ReportStderr(progress, result.StandardError);
            throw Fail(operation, vmName, $"qm exited with {result.ExitCode}");
        }
    }

    /// <summary>One <c>pvesh</c> query, answered as JSON.</summary>
    private async Task<JsonElement> PveshAsync(
        string operation,
        string vmName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync(
            _options.Proxmox.PveshPath, [.. arguments, "--output-format", "json"], standardInput: null,
            QueryTimeout, standardOutputLines: null, cancellationToken).ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw Fail(operation, vmName, $"pvesh timed out after {QueryTimeout.TotalSeconds:0} seconds");
        }

        if (result.ExitCode != 0)
        {
            throw Fail(operation, vmName, $"pvesh exited with {result.ExitCode}");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Fail(operation, vmName, "pvesh did not answer with JSON");
        }
    }

    private static void ReportStderr(IProgress<string>? progress, string standardError)
    {
        if (progress is null || string.IsNullOrWhiteSpace(standardError))
        {
            return;
        }

        var shown = 0;
        foreach (var line in standardError.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            progress.Report("qm: " + text);
            if (++shown == 6)
            {
                break;
            }
        }
    }

    private ProxmoxOperationException Fail(string operation, string vmName, string reason)
    {
        _logger.LogError(
            "Proxmox driver operation {Operation} for {Vm} failed: {Reason}",
            operation,
            vmName.Length == 0 ? "-" : vmName,
            reason);

        return new ProxmoxOperationException(operation, vmName, reason);
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
