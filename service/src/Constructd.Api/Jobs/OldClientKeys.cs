using System.Text.Json.Nodes;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>
/// The old client's root key for a VM of a host that ran the old stack and updated itself in place. The old host service
/// never dialled its guests: the old client on the user's PC provisioned them as root with its own key per VM, and at the
/// end of provisioning it removed the bootstrap key from the seed user (<c>bin/build-autoinstall-iso.sh</c>), so after the
/// update the engine reaches none of them. The device agent hands over that key (and the old client's settings for the VM)
/// before it asks for their reprovision, as it hands the key to an adoption on the PC (<see cref="LocalVmAdoption"/>).
/// <para>
/// The engine keeps what works: when it already reaches the VM (with a key it keeps, or as the seed user), a different
/// key is not stored, and the same key sent again changes nothing. Otherwise the key is stored
/// (<see cref="ProvisionTargets.AdoptedKeyPath"/>) only once it proved to reach the VM as root. The old client's settings
/// become the VM's stored settings only when the engine has none for it.
/// </para>
/// </summary>
public static class OldClientKeys
{
    /// <summary><c>stored</c>: the key now reaches the VM. <c>kept</c>: the engine already keeps this key and it works. <c>reachable</c>: the engine reaches the VM without it.</summary>
    public sealed record Outcome(string Status);

    /// <summary>Why the key was not taken. The message carries nothing of the key or of what the guest said.</summary>
    public sealed class Refusal(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
    }

    /// <summary>The longest key text taken (an RSA 4096 key in OpenSSH form is about 3.4 KB).</summary>
    public const int MaxKeyLength = 16 * 1024;

    /// <summary>Reads the guest's coding agents (<c>AI_TOOLS</c>) while it proves the login works.</summary>
    public const string ProbeCommand = "{ sed -n 's/^AI_TOOLS=//p' /etc/construct/config.env 2>/dev/null | head -n 1; true; }";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);

    public static bool ValidKey(string? key) =>
        key is { Length: > 0 and <= MaxKeyLength } && key.Contains("PRIVATE KEY", StringComparison.Ordinal) && !key.Contains('\0');

    public static async Task<Outcome> HandOverAsync(IServiceProvider services, Vm vm, string privateKey, JsonObject? settings, JsonObject? instanceState,
        string actor, CancellationToken ct)
    {
        var options = services.GetRequiredService<ConstructdOptions>();
        var targets = services.GetRequiredService<IProvisionTargets>();
        var ssh = services.GetRequiredService<ISshRunner>();
        if (services.GetRequiredService<IHypervisorDriver>() is ExternalEndpointDriver)
            throw new Refusal(409, "unsupported-capability", "The engine reaches an external machine with its configured account.");
        if (await services.GetRequiredService<ProvisionStart>().StoppedAsync(vm, ct))
            throw new Refusal(409, "vm-not-running", "The VM is not running; start it first.");

        // Where the engine dials the VM now: as root with the key it keeps, or as its seed account.
        var current = await targets.ResolveAsync(vm, ct)
            ?? throw new Refusal(409, "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");
        var keyFile = ProvisionTargets.AdoptedKeyPath(options, vm.Name);
        string status;
        var read = await ProbeAsync(ssh, current, ct);
        if (read is not null)
        {
            var same = current.IsRoot && current.IdentityFile == keyFile && File.Exists(keyFile) &&
                await File.ReadAllTextAsync(keyFile, ct) == ProvisionTargets.KeyText(privateKey);
            status = same ? "kept" : "reachable";
        }
        else
        {
            var trial = keyFile + ".trial-" + Guid.NewGuid().ToString("n");
            try
            {
                await ProvisionTargets.WriteKeyAsync(trial, privateKey, ct);
                read = await ProbeAsync(ssh, new SshTarget(current.Host, current.Port, "root", trial, current.KnownHostsFile), ct)
                    ?? throw new Refusal(409, "key-rejected", "The key does not reach the VM as root; nothing was stored.");
                // What the engine kept before does not reach the VM any more (or there was none): this one does.
                File.Move(trial, keyFile, overwrite: true);
                status = "stored";
            }
            finally
            {
                File.Delete(trial);
            }
        }

        // The old client's choices for the VM, when the engine has none (the old stack stored none), so a reprovision can run.
        var desired = services.GetRequiredService<IVmDesiredStateSource>();
        if ((settings is not null || instanceState is not null) && await desired.GetDesiredForAsync(vm, ct) is null)
        {
            var given = LegacyClientSettings.ToDesired(settings, instanceState) with { CodingAgents = CodingAgents.FromAiTools(read.FirstOrDefault()) };
            await desired.SetDesiredAsync(vm.Name, new StoredDesiredState(given, ProvisionScript.DesiredHash(given),
                services.GetRequiredService<IClock>().UtcNow, actor, vm.Created), ct);
        }

        return new Outcome(status);
    }

    /// <summary>The probe's output lines when the login worked; null when it did not.</summary>
    private static async Task<List<string>?> ProbeAsync(ISshRunner ssh, SshTarget target, CancellationToken ct)
    {
        var lines = new Lines();
        try
        {
            return await ssh.RunAsync(target, ProbeCommand, null, lines, ProbeTimeout, ct) == 0 ? lines.Items : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private sealed class Lines : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) lock (Items) Items.Add(value.Trim()); }
    }
}
