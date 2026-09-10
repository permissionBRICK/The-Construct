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
}
