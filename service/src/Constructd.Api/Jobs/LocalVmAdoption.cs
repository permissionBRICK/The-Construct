using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Jobs;

/// <summary>Why an adoption stopped. The message is composed here and carries no value from a file or a guest.</summary>
public sealed class VmAdoptionException(string code, string message) : Exception(message), IConstructdError
{
    /// <summary><c>invalid</c>, <c>not-found</c> or <c>conflict</c>; anything later is <c>guest</c>.</summary>
    public string Code { get; } = code;
}

/// <param name="Settings">The old client's <c>.construct-settings.json</c>.</param>
/// <param name="InstanceState">The named instance's <c>instances\&lt;name&gt;.json</c>; null for the default instance.</param>
/// <param name="SshPrivateKey">The old client's key for <c>root</c> on the VM (OpenSSH private key text).</param>
public sealed record LocalAdoptionRequest(string Name, string Owner, JsonObject? Settings, JsonObject? InstanceState, string SshPrivateKey);

public sealed record LocalAdoptionResult(string Name, string Owner, int? SshPort, string Incarnation, int Cpu, int RamMb, int DiskGb, string ServiceUrl);

/// <summary>
/// Adopts a VM the old client made on this PC, by name, without rebuilding or restarting it — what
/// <c>service/host/ConvertTo-ConstructHost.ps1</c> does with <c>vms adopt</c> and
/// <c>bin/adopt-host.py</c>, done by the engine itself. The VM is registered with the CPU, memory and
/// disk Hyper-V reports, the old client's provisioning choices become its stored desired state, and the
/// guest gets the service settings and its VM token through <c>bin/adopt-host.py</c>, which verifies
/// the engine from inside the guest and restores its files when it fails. A VM that is off, saved or paused
/// is started first and stays running. Nothing here powers the VM off, changes its hardware or deletes
/// anything; a repeat resumes the same VM and refuses another.
/// The admin CLI (<c>vms adopt-local</c>) and the <c>adopt-vm</c> job both run this.
/// </summary>
public static class LocalVmAdoption
{
    public const string JobKind = "adopt-vm";

    /// <summary>The guest helpers <c>adopt-host.py</c> installs; the build copies them to <c>guest\</c>.</summary>
    public static readonly string[] GuestHelpers =
        ["construct", "construct-vm.sh", "construct-expose.sh", "construct-secret.sh", "construct-config.sh", "construct-idle-report.sh", "construct-backup.sh"];

    public static string GuestDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "guest");

    /// <summary>The checks that need no hypervisor, made before a job is queued.</summary>
    public static async Task<User> ValidateAsync(IServiceProvider services, LocalAdoptionRequest request, CancellationToken ct)
    {
        if (!VmNameValidator.IsValid(request.Name)) throw new VmAdoptionException("invalid", "The VM name is not a valid instance name.");
        if (string.IsNullOrWhiteSpace(request.SshPrivateKey) || !request.SshPrivateKey.Contains("PRIVATE KEY", StringComparison.Ordinal))
            throw new VmAdoptionException("invalid", "The SSH key of the old client is required to reach the VM.");
        if (services.GetService<IVmFactsDriver>() is null)
            throw new VmAdoptionException("conflict", "Adoption needs the Hyper-V platform.");
        var user = await services.GetRequiredService<IUserStore>().GetAsync(request.Owner, ct);
        if (user is null || !user.Enabled || user.Role != Role.Admin)
            throw new VmAdoptionException("conflict", "The adopting owner must be an enabled host admin.");
        return user;
    }

    public static async Task<LocalAdoptionResult> AdoptAsync(IServiceProvider services, LocalAdoptionRequest request, string actor,
        IProgress<string> progress, CancellationToken ct)
    {
        var user = await ValidateAsync(services, request, ct);
        var options = services.GetRequiredService<ConstructdOptions>();
        var vms = services.GetRequiredService<IVmRepository>();
        var clock = services.GetRequiredService<IClock>();
        var name = request.Name;

        progress.Report($"Reading VM '{name}' from Hyper-V");
        var facts = await services.GetRequiredService<IVmFactsDriver>().GetFactsAsync(name, ct)
            ?? throw new VmAdoptionException("not-found", $"Hyper-V has no VM named '{name}'.");
        if (facts.State is not (VmState.Running or VmState.Off or VmState.Saved or VmState.Paused))
            throw new VmAdoptionException("conflict", "Hyper-V reports the VM neither running nor stopped; retry once it is.");
        if (!Guid.TryParse(facts.Id, out var incarnation)) throw new VmAdoptionException("conflict", "Hyper-V reported no usable VM id.");
        var ramMb = (int)(facts.MemoryBytes / (1024 * 1024));
        var diskGb = (int)Math.Ceiling(facts.DiskBytes / (double)(1L << 30));
        if (facts.Cpu < 1 || ramMb < 128 || diskGb < 1) throw new VmAdoptionException("conflict", "Hyper-V reported no usable CPU, memory or disk size.");

        var current = await vms.GetAsync(name, ct);
        if (current is not null && (current.Kind != VmKind.Primary || current.Deleting ||
            !string.Equals(current.Owner, user.Name, StringComparison.OrdinalIgnoreCase) ||
            current.Incarnation != incarnation.ToString("D")))
            throw new VmAdoptionException("conflict", "This name already belongs to a different managed VM.");
        // A VM the old client left off, saved or paused is started here (its owner expects the move to bring it up) and
        // stays running: the reprovision follows.
        if (facts.State != VmState.Running) facts = await StartAsync(services, options, name, progress, ct);
        if (current is null)
        {
            current = new Vm(name, user.Name, facts.Cpu, (int)Math.Ceiling(ramMb / 1024d), diskGb, clock.UtcNow, facts.State, null, null,
                IdlePolicy.Disabled, Vm.NoForwards, TokenKind: VmTokenKind.Primary, RamMb: ramMb, Incarnation: incarnation.ToString("D"));
            var added = await vms.AddAsync(current, user.MaxVms, ct);
            if (added != VmAddOutcome.Added) throw new VmAdoptionException("conflict", "Adoption refused: " + added + ".");
            progress.Report($"Registered with {facts.Cpu} vCPUs, {ramMb} MiB RAM and {diskGb} GiB disk, as Hyper-V reports them");
        }

        var port = await services.GetRequiredService<IPortForwardManager>().AllocateSshForwardAsync(name, ct);
        progress.Report($"SSH forward {options.PublicHost}:{port}");

        var certificate = TlsComposition.CurrentCertificatePem(options)
            ?? throw new VmAdoptionException("conflict", "The engine has no TLS certificate for the guest to verify.");
        var serviceUrl = $"https://{options.PublicHost}:{options.ApiPort()}";

        var guest = await ReachGuestAsync(services, options, name, facts, request.SshPrivateKey, progress, ct);

        // The old client's choices, the coding agents the VM has, and what keeps a later reprovision from the engine
        // service-managed.
        var desired = LegacyClientSettings.ToDesired(request.Settings, request.InstanceState);
        desired = desired with
        {
            Settings = desired.Settings with
            {
                ServiceUrl = serviceUrl,
                ServiceCaB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(certificate)),
                ExternalHost = options.PublicHost,
                ExternalSshPort = port,
            },
            CodingAgents = guest.CodingAgents,
        };
        // Tied to this VM's record, so a later VM of the same name does not inherit them.
        var created = (await vms.GetAsync(name, ct))?.Created;
        await services.GetRequiredService<IVmDesiredStateSource>().SetDesiredAsync(name,
            new StoredDesiredState(desired, ProvisionScript.DesiredHash(desired), clock.UtcNow, actor, created), ct);
        progress.Report($"Stored the provisioning settings the old client kept ({desired.Projects.Count} project(s))");

        await EnrollGuestAsync(services, guest, name, user.Name, port, serviceUrl, certificate, options, progress, ct);

        if (await vms.GetAsync(name, ct) is { } registered)
        {
            await vms.UpdateAsync(registered with { SshForwardPort = port, State = VmState.Running }, ct);
            // The old client let every VM start with the host; the engine's rule decides from now on.
            if (services.GetService<HostStartSettings>() is { } hostStart) await hostStart.TryApplyAsync(registered, ct);
        }
        await services.GetRequiredService<IAuditLog>().AppendAsync(new AuditEntry(clock.UtcNow, actor, "vm.adopt", name, AuditOutcome.Success,
            "existing Hyper-V VM, guest enrolled; owner=" + user.Name), ct);
        progress.Report("VM adopted; it keeps running");
        return new LocalAdoptionResult(name, user.Name, port, incarnation.ToString("D"), facts.Cpu, ramMb, diskGb, serviceUrl);
    }

    /// <summary>
    /// Starts the VM (Hyper-V's start also resumes a saved or paused VM; it works by name, so the engine need not know the
    /// VM yet) and waits, as a provision that starts its VM does, until the guest answers on its SSH port. Its facts once
    /// it runs. A VM that started but does not answer in time keeps running.
    /// </summary>
    private static async Task<HypervisorVmFacts> StartAsync(IServiceProvider services, ConstructdOptions options, string name,
        IProgress<string> progress, CancellationToken ct)
    {
        var driver = services.GetRequiredService<IHypervisorDriver>();
        progress.Report("Starting the VM");
        try
        {
            await driver.StartAsync(name, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VmAdoptionException("conflict", "Hyper-V could not start the VM; start it in Hyper-V Manager to see why.");
        }

        if (await driver.GetStateAsync(name, ct) != VmState.Running)
            throw new VmAdoptionException("conflict", "The VM did not start; start it in Hyper-V Manager to see why.");
        var wait = TimeSpan.FromSeconds(Math.Max(1, options.StartedVmSshWaitSeconds));
        progress.Report("Waiting for the VM to answer over SSH");
        if (!await driver.WaitReachableAsync(name, wait, null, ct))
            throw new VmAdoptionException("guest", $"The VM started but did not answer over SSH within {wait.TotalMinutes:0.#} minutes. It keeps running; retry once it is up.");
        progress.Report("The VM answers over SSH");
        return await services.GetRequiredService<IVmFactsDriver>().GetFactsAsync(name, ct)
            ?? throw new VmAdoptionException("not-found", $"Hyper-V has no VM named '{name}'.");
    }

    /// <summary>The guest reached with the old client's key and proven to be this VM, with the coding agents it has.</summary>
    private sealed record ReachedGuest(SshTarget Target, string MachineId, IReadOnlyList<string>? CodingAgents);

    private static async Task<ReachedGuest> ReachGuestAsync(IServiceProvider services, ConstructdOptions options, string name,
        HypervisorVmFacts facts, string privateKey, IProgress<string> progress, CancellationToken ct)
    {
        var endpoint = await services.GetRequiredService<IHypervisorDriver>().GetEndpointAsync(name, ct)
            ?? throw new VmAdoptionException("guest", "The VM has no SSH endpoint.");
        var ssh = services.GetRequiredService<ISshRunner>();

        // The engine's own copy of the old client's key, readable by the engine only (ssh refuses it otherwise).
        // Later jobs reach the VM with it too (ProvisionTargets): it has no seed user. Kept there only once it
        // proved to reach this VM, so a failed attempt never replaces a key that works.
        var keyFile = ProvisionTargets.AdoptedKeyPath(options, name);
        var keyDir = Path.GetDirectoryName(keyFile)!;
        var trial = keyFile + ".trial";
        await ProvisionTargets.WriteKeyAsync(trial, privateKey, ct);
        var target = new SshTarget(endpoint.SshHost, endpoint.SshPort, "root", trial, Path.Combine(keyDir, "known_hosts"));

        try
        {
            // The guest the key reached has to be this VM: its firmware id is the one Hyper-V reports. The coding agents it
            // was provisioned with (AI_TOOLS in its config) follow, when it names them.
            var identity = new Lines();
            var read = await ssh.RunAsync(target, IdentityCommand, null, identity, TimeSpan.FromMinutes(2), ct);
            if (read != 0 || identity.Items.Count is not (2 or 3)) throw new VmAdoptionException("guest", $"Could not read the guest's identity over SSH (exit {read}).");
            var machineId = identity.Items[0].Trim();
            if (!Guid.TryParse(identity.Items[1].Trim(), out var guestFirmware) || !Guid.TryParse(facts.FirmwareId, out var vmFirmware) ||
                guestFirmware == Guid.Empty || guestFirmware != vmFirmware)
                throw new VmAdoptionException("guest", "The guest reached over SSH is not this Hyper-V VM; nothing was changed in it.");
            progress.Report("The guest's firmware id matches the Hyper-V VM");
            File.Move(trial, keyFile, overwrite: true);
            return new ReachedGuest(target with { IdentityFile = keyFile }, machineId, identity.Items.Count == 3 ? CodingAgents.FromAiTools(identity.Items[2]) : null);
        }
        finally
        {
            File.Delete(trial);
        }
    }

    public const string IdentityCommand =
        "cat /etc/machine-id /sys/class/dmi/id/product_uuid && { sed -n 's/^AI_TOOLS=//p' /etc/construct/config.env 2>/dev/null | head -n 1; true; }";

    private static async Task EnrollGuestAsync(IServiceProvider services, ReachedGuest guest, string name, string owner, int port,
        string serviceUrl, string certificate, ConstructdOptions options, IProgress<string> progress, CancellationToken ct)
    {
        var ssh = services.GetRequiredService<ISshRunner>();
        var (target, machineId) = (guest.Target, guest.MachineId);

        // Issued only once the guest is known; a repeat replaces the earlier token.
        var token = await ((IVmTokenIssuer)services.GetRequiredService<ITokenService>()).IssueVmTokenAsync(name, VmTokenKind.Primary, ct);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var helper in GuestHelpers) files[helper] = await File.ReadAllTextAsync(Path.Combine(GuestDirectory, helper), ct);
        var payload = JsonSerializer.Serialize(new
        {
            name, owner, machineId, serviceUrl, publicHost = options.PublicHost, sshPort = port, vmToken = token, certificate, files,
        });
        var code = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(GuestDirectory, "adopt-host.py"), ct));
        var said = new Lines();
        var exit = await ssh.RunAsync(target, $"python3 -c \"import base64;exec(base64.b64decode('{code}'))\" 2>&1", payload, said, TimeSpan.FromMinutes(2), ct);
        // adopt-host.py prints only fixed, credential-free messages.
        foreach (var line in said.Items) progress.Report("guest: " + line);
        if (exit != 0) throw new VmAdoptionException("guest", $"The guest could not be switched to the engine (exit {exit}); it restored its files.");

        // The account's backup key, so the VM encrypts its backups to the account from now on. Not required: a device enrols it later.
        // The admin CLI runs without the participation store: nothing to enrol there.
        if (services.GetService<Constructd.Sync.Participation.EngineKeyStore>()?.BackupKey(owner) is { } backupKey)
        {
            var added = await ssh.RunAsync(target, BackupScript.RecipientsAdd(Constructd.Api.Devices.AccountBackupKeys.Label), backupKey, null, TimeSpan.FromMinutes(2), ct);
            progress.Report(added == 0 ? "The account's backup key is enrolled" : $"The account's backup key was not enrolled (exit {added}); a device enrols it later");
        }
    }

    private sealed class Lines : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) Items.Add(value.TrimEnd()); }
    }
}
