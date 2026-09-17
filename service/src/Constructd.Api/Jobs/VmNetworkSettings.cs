using System.Net;
using System.Net.Sockets;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Api.Jobs;

/// <summary>Desired and applied settings are tied to the VM's creation identity. Callers hold its gate for writes.</summary>
public sealed class VmNetworkSettings(IHostConfigStore config, IHostNetworkPolicy policy,
    ConstructdOptions options, IHypervisorDriver driver)
{
    public sealed record Setting(DateTimeOffset Created, string? Mode, string? Address, string? Gateway, string[]? Dns);
    public sealed record View(string? Mode, string EffectiveMode, string? Address, string? PendingMode,
        string? PendingAddress, string? DesiredAddress, string? Gateway, string[]? Dns, bool Pending,
        bool MaySwitchMode, bool MaySetAddress, string AppliesOn = "next-stop-start");
    public bool Supported => options.IsProxmox;
    private static string Section(Vm vm, bool applied = false) =>
        (applied ? "network-applied:" : "network:") + vm.Name.ToLowerInvariant();

    public async Task<Setting?> GetAsync(Vm vm, CancellationToken ct, bool applied = false)
    {
        var setting = await config.GetAsync<Setting>(Section(vm, applied), ct);
        return setting?.Created == vm.Created ? setting : null;
    }

    public Task SaveAsync(Vm vm, Setting setting, string actor, CancellationToken ct) =>
        config.SetAsync(Section(vm), setting with { Created = vm.Created }, actor, ct);

    public async Task<Setting> DesiredAsync(Vm vm, CancellationToken ct)
    {
        var desired = await GetAsync(vm, ct) ?? new(vm.Created, null, null, null, null);
        var mode = Supported ? desired.Mode ?? (await policy.GetAsync(ct)).DefaultMode : "relayed";
        return desired with { Mode = mode, Address = mode == "direct" ? desired.Address : null,
            Gateway = mode == "direct" ? desired.Gateway : null, Dns = mode == "direct" ? desired.Dns : null };
    }

    public async Task<Setting> CurrentAsync(Vm vm, CancellationToken ct) =>
        Supported ? await GetAsync(vm, ct, applied: true) ?? new(vm.Created, "relayed", null, null, null)
            : new(vm.Created, "relayed", null, null, null);

    public async Task<string?> AddressAsync(Vm vm, Setting current, CancellationToken ct) =>
        current.Mode != "direct" ? null : current.Address?.Split('/')[0] ?? (await driver.GetEndpointAsync(vm.Name, ct))?.SshHost;

    public async Task<View> ProjectAsync(Vm vm, bool admin, CancellationToken ct)
    {
        var setting = await GetAsync(vm, ct);
        var desired = await DesiredAsync(vm, ct);
        var current = await CurrentAsync(vm, ct);
        var pending = !Same(current, desired);
        return new(setting?.Mode, current.Mode!, await AddressAsync(vm, current, ct),
            current.Mode != desired.Mode ? desired.Mode : null,
            current.Address != desired.Address ? desired.Address : null,
            setting?.Address, setting?.Gateway, setting?.Dns, pending,
            admin || (await policy.GetAsync(ct)).OwnerMaySwitchMode, admin);
    }

    public static bool Same(Setting left, Setting right) => left.Mode == right.Mode &&
        left.Address == right.Address && left.Gateway == right.Gateway && (left.Dns ?? []).SequenceEqual(right.Dns ?? []);

    public static string? Validate(Setting value)
    {
        if (value.Mode is not (null or "relayed" or "direct")) return "Mode must be null, relayed or direct.";
        if (value.Address is not null)
        {
            var parts = value.Address.Split('/');
            if (parts.Length != 2 || !IPv4(parts[0]) || !int.TryParse(parts[1], out var prefix) || prefix is < 1 or > 32)
                return "Address must be an IPv4 CIDR with a prefix from 1 to 32.";
        }
        if ((value.Address is null) != (value.Gateway is null) || value.Gateway is not null && !IPv4(value.Gateway))
            return "A fixed address and IPv4 gateway must be supplied together.";
        if (value.Dns is not null && (value.Dns.Length > 8 || value.Dns.Any(d => !IPv4(d))))
            return "DNS must contain at most eight IPv4 addresses.";
        return null;
    }

    private static bool IPv4(string? text) => IPAddress.TryParse(text, out var ip) &&
        ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == text &&
        !IPAddress.IsLoopback(ip) && ip.GetAddressBytes()[0] is > 0 and < 224 &&
        !text.StartsWith("169.254.", StringComparison.Ordinal);
}
