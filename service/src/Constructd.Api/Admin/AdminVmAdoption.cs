using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Admin;

public static partial class AdminCli
{
    // Offline installation command. The elevated coordinator verifies the live Hyper-V
    // GUID and hardware and keeps constructd stopped until guest enrollment is ready.
    // Failure never deletes or powers off the existing VM. A repeat can only resume the
    // same owner/incarnation; it must never take over an unrelated managed VM.
    private static async Task<int> AdoptVmAsync(IReadOnlyList<string> args,
        IServiceProvider services, AdminOutput writer, CancellationToken ct)
    {
        if (services.GetRequiredService<Constructd.Core.Configuration.ConstructdOptions>().IsProxmox)
            return writer.Error(AdminExitCode.Conflict, "Adoption requires an existing Hyper-V VM.");
        if (!writer.Json) return writer.Usage("adoption requires --json for its one-time credential handoff");
        var name = args.ElementAtOrDefault(2);
        var opts = new OptionSet(args.Skip(3),
            valueOptions: ["--owner", "--cpu", "--ram-mb", "--disk-gb", "--incarnation"], flags: []);
        if (!VmNameValidator.IsValid(name) || opts.Unknown is not null ||
            opts.Value("--owner") is not { Length: > 0 } owner ||
            !int.TryParse(opts.Value("--cpu"), out var cpu) || cpu < 1 || cpu > 2048 ||
            !int.TryParse(opts.Value("--ram-mb"), out var ram) || ram < 128 || ram > 16777216 ||
            !int.TryParse(opts.Value("--disk-gb"), out var disk) || disk < 1 ||
            !Guid.TryParse(opts.Value("--incarnation"), out var incarnation))
            return writer.Usage("adopt requires a valid name, owner, CPU, RAM in MiB, disk in GiB and Hyper-V GUID");
        var user = await services.GetRequiredService<IUserStore>().GetAsync(owner, ct);
        if (user is null || !user.Enabled || user.Role != Role.Admin)
            return writer.Error(AdminExitCode.Conflict, "the adopting owner must be an enabled host admin");
        var vms = services.GetRequiredService<IVmRepository>();
        var current = await vms.GetAsync(name!, ct);
        if (current is not null && (current.Kind != VmKind.Primary || current.Deleting ||
            !string.Equals(current.Owner, owner, StringComparison.OrdinalIgnoreCase) ||
            current.Incarnation != incarnation.ToString("D")))
            return writer.Error(AdminExitCode.Conflict, "this name already belongs to a different managed VM");
        var state = await services.GetRequiredService<IHypervisorDriver>().GetStateAsync(name!, ct);
        if (state != VmState.Running)
            return writer.Error(AdminExitCode.Conflict, "the existing VM must be running before adoption");
        if (current is null)
        {
            current = new Vm(name!, owner, cpu, (int)Math.Ceiling(ram / 1024d), disk,
                services.GetRequiredService<IClock>().UtcNow, state, null, null,
                IdlePolicy.Disabled, Vm.NoForwards, TokenKind: VmTokenKind.Primary,
                RamMb: ram, Incarnation: incarnation.ToString("D"));
            var added = await vms.AddAsync(current, user.MaxVms, ct);
            if (added != VmAddOutcome.Added)
                return writer.Error(AdminExitCode.Conflict, "adoption refused: " + added);
        }
        var port = await services.GetRequiredService<IPortForwardManager>().AllocateSshForwardAsync(name!, ct);
        var issuer = (IVmTokenIssuer)services.GetRequiredService<ITokenService>();
        var token = await issuer.IssueVmTokenAsync(name!, VmTokenKind.Primary, ct);
        await AuditAsync(services, "vm.adopt", name!, "existing Hyper-V VM; owner=" + owner, ct);
        return writer.Result(new { name, owner, sshPort = port, vmToken = token, incarnation },
            "VM adopted; use --json to receive its enrollment credential.");
    }

    // A VM the old client made on this PC, adopted whole by the engine (Jobs/LocalVmAdoption.cs): it reads
    // CPU, memory and disk from Hyper-V, stores the old client's settings as the VM's desired state and
    // switches the guest over with bin/adopt-host.py. The guest verifies the running service, so this
    // runs while the service runs. Progress goes to stderr; the result is the one stdout answer.
    private static async Task<int> AdoptLocalVmAsync(IReadOnlyList<string> args, IServiceProvider services, AdminOutput writer,
        TextWriter error, CancellationToken ct)
    {
        var name = args.ElementAtOrDefault(2);
        var opts = new OptionSet(args.Skip(3), valueOptions: ["--owner", "--settings", "--instance-state", "--ssh-key"], flags: []);
        if (name is null || opts.Unknown is not null || opts.Value("--owner") is not { Length: > 0 } owner ||
            opts.Value("--settings") is not { Length: > 0 } settingsPath || opts.Value("--ssh-key") is not { Length: > 0 } keyPath)
            return writer.Usage("adopt-local requires a VM name, --owner, --settings and --ssh-key");
        JsonObject? settings, state = null;
        string key;
        try
        {
            settings = ReadObject(settingsPath);
            if (opts.Value("--instance-state") is { Length: > 0 } statePath) state = ReadObject(statePath);
            key = await File.ReadAllTextAsync(keyPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return writer.Usage("adopt-local could not read one of its files (" + ex.GetType().Name + ")");
        }
        try
        {
            var result = await LocalVmAdoption.AdoptAsync(services, new LocalAdoptionRequest(name, owner, settings, state, key),
                "admin-cli", new Progress(error), ct);
            return writer.Result(result, $"VM {result.Name} adopted for {result.Owner}; SSH forward {result.SshPort}.");
        }
        catch (VmAdoptionException ex)
        {
            return writer.Error(ex.Code switch
            {
                "invalid" => AdminExitCode.Usage,
                "not-found" => AdminExitCode.NotFound,
                "conflict" => AdminExitCode.Conflict,
                _ => AdminExitCode.Failed,
            }, ex.Message);
        }
    }

    private static JsonObject ReadObject(string path)
    {
        // The old client writes these with Windows PowerShell 5.1, which can add a BOM.
        var text = File.ReadAllText(path).TrimStart('\uFEFF');
        return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("not a JSON object");
    }

    /// <summary>Synchronous on purpose: the lines reach stderr in order, as they happen.</summary>
    private sealed class Progress(TextWriter error) : IProgress<string>
    {
        public void Report(string value) => error.WriteLine(value);
    }
}
