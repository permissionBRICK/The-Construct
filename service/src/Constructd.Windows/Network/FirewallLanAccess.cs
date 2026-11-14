using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Windows.Network;

/// <summary>
/// LAN access of a solo engine as one inbound Windows Firewall rule: TCP on the API port, private
/// profile only. The installer's own rule admits the PC's VMs on the Default Switch interface; this one
/// adds the private network. <c>Uninstall-ConstructHost.ps1</c> removes both by name.
/// </summary>
public sealed class FirewallLanAccess(IProcessRunner processes, ConstructdOptions options) : ILanAccess
{
    /// <summary>The rule's name; no spaces, so it travels to netsh as one plain token.</summary>
    public const string RuleName = "ConstructEngineLan";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<bool> GetAsync(CancellationToken ct)
    {
        // netsh exits 0 when a rule of that name exists and 1 ("No rules match") when none does.
        var shown = await processes.RunAsync("netsh.exe", ["advfirewall", "firewall", "show", "rule", "name=" + RuleName], null, Timeout, null, ct);
        return shown.Succeeded;
    }

    public async Task SetAsync(bool enabled, CancellationToken ct)
    {
        // Delete first either way, so a repeat never leaves two rules.
        await processes.RunAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName], null, Timeout, null, ct);
        if (!enabled) return;
        var added = await processes.RunAsync("netsh.exe",
            ["advfirewall", "firewall", "add", "rule", "name=" + RuleName, "dir=in", "action=allow", "protocol=TCP",
                "localport=" + options.ApiPort().ToString(System.Globalization.CultureInfo.InvariantCulture), "profile=private"],
            null, Timeout, null, ct);
        if (!added.Succeeded) throw new InvalidOperationException($"netsh could not add the LAN firewall rule (exit {added.ExitCode}).");
    }
}
