using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Microsoft.Extensions.Logging;

namespace Constructd.Proxmox;

/// <summary>Snapshots host adapter facts and untrusted guest-agent reports for managed incarnations.</summary>
public sealed class ProxmoxGuestAddressProvider(IProcessRunner runner, ConstructdOptions options,
    IVmRepository vms, IClock clock, ILogger<ProxmoxGuestAddressProvider>? logger = null)
    : IGuestAddressProvider, IGuestAddressSnapshotProvider
{
    private readonly ProxmoxCommands commands = new(runner, options);
    public async Task<IGuestAddressProvider> CaptureAsync(CancellationToken ct)
    {
        var captured = new Captured();
        var bridges = new HashSet<string>(StringComparer.Ordinal) { options.Proxmox.Bridge };
        try
        {
            var resources = await commands.ResourcesAsync(ct);
            var managed = (await vms.ListAsync(null, ct)).Where(v => !v.Deleting && v.State != VmState.Absent &&
                (v.Kind == VmKind.Primary || v.Incarnation is not null));
            foreach (var vm in managed)
            {
                try
                {
                    var id = commands.Find(resources, vm.Name);
                    if (id is null) { Warn(vm.Name, "vm-not-found"); continue; }
                    var config = await commands.ConfigAsync(id.Value, ct);
                    var uuid = ProxmoxCommands.Incarnation(config);
                    if (uuid is null || !string.Equals(ProxmoxCommands.String(config, "name"), vm.Name, StringComparison.OrdinalIgnoreCase) ||
                        vm.Incarnation is not null && !string.Equals(uuid, vm.Incarnation, StringComparison.OrdinalIgnoreCase))
                    { Warn(vm.Name, "vm-incarnation-changed"); continue; }
                    var adapters = ParseAdapters(config, uuid);
                    captured.Adapters[vm.Name] = adapters;
                    foreach (var adapter in adapters) if (adapter.SwitchName is { } bridge) bridges.Add(bridge);
                    var agent = await commands.RunAsync(options.Proxmox.QmPath,
                        ["agent", ProxmoxCommands.Number(id.Value), "network-get-interfaces"], ct, TimeSpan.FromSeconds(15));
                    if (!agent.Succeeded) { Warn(vm.Name, "guest-agent-unavailable"); continue; }
                    using var json = JsonDocument.Parse(agent.StandardOutput);
                    captured.Reported[vm.Name] = ParseAddresses(json.RootElement, adapters, clock.UtcNow);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { Warn(vm.Name, "vm-query-failed"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { Warn(null, "guest-snapshot-unavailable"); }
        try
        {
            // Read ALL host addresses: a different host interface is also forbidden as a guest destination.
            using var json = await IpAsync(["-j", "addr", "show"], ct);
            foreach (var iface in json.RootElement.EnumerateArray())
            {
                var name = ProxmoxCommands.String(iface, "ifname");
                if (!iface.TryGetProperty("addr_info", out var addresses) || addresses.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in addresses.EnumerateArray())
                {
                    if (GuestAddressRules.Parse(ProxmoxCommands.String(entry, "local") ?? "") is not { } ip) continue;
                    captured.Host.Add(ip);
                    if (name is not null && bridges.Contains(name) && entry.TryGetProperty("prefixlen", out var prefix) && prefix.TryGetInt32(out var bits) &&
                        bits >= 0 && bits <= ip.GetAddressBytes().Length * 8)
                        captured.Subnets.Add(new(ip + "/" + bits, name, name));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { captured.Host.Clear(); captured.Subnets.Clear(); Warn(null, "host-addresses-unavailable"); }
        try
        {
            using var json = await IpAsync(["-j", "neigh", "show"], ct);
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                var dev = ProxmoxCommands.String(entry, "dev"); var mac = ProxmoxCommands.String(entry, "lladdr");
                if (dev is null || !bridges.Contains(dev) || mac is null || GuestAddressRules.Parse(ProxmoxCommands.String(entry, "dst") ?? "") is not { } ip) continue;
                var state = entry.TryGetProperty("state", out var status) && status.ValueKind == JsonValueKind.Array
                    ? string.Join(',', status.EnumerateArray().Select(s => s.GetString())) : ProxmoxCommands.String(entry, "state") ?? "unknown";
                captured.Neighbors.Add(new(ip.ToString(), mac, dev, state));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { captured.Neighbors.Clear(); Warn(null, "host-neighbors-unavailable"); }
        return captured;
    }
    private async Task<JsonDocument> IpAsync(string[] args, CancellationToken ct)
    {
        var result = await commands.RunAsync("ip", args, ct, TimeSpan.FromSeconds(15));
        if (!result.Succeeded) throw ProxmoxCommands.Failure();
        return JsonDocument.Parse(result.StandardOutput);
    }
    private void Warn(string? vm, string category) => logger?.LogWarning("Guest network snapshot unavailable for {Vm} ({Category}).", vm ?? "host", category);
    private static string Mac(string value) => value.Replace(":", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
    private static IReadOnlyList<GuestAdapter> ParseAdapters(JsonElement config, string uuid)
    {
        var result = new List<GuestAdapter>();
        foreach (var entry in config.EnumerateObject())
        {
            if (!Regex.IsMatch(entry.Name, @"\Anet\d+\z") || entry.Value.ValueKind != JsonValueKind.String) continue;
            var text = entry.Value.GetString()!;
            var mac = text.Split(',')[0].Split('=').Last();
            if (Mac(mac).Length != 12 || !Mac(mac).All(Uri.IsHexDigit)) continue;
            // No MAC filter is enforced by this backend, even when an administrator enables the PVE firewall.
            result.Add(new(uuid, entry.Name, mac, true, ProxmoxCommands.Property(text, "bridge")));
        }
        return result;
    }
    private static IReadOnlyList<GuestAddress> ParseAddresses(JsonElement json, IReadOnlyList<GuestAdapter> adapters, DateTimeOffset now)
    {
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("result", out var result)) json = result;
        if (json.ValueKind != JsonValueKind.Array) return [];
        var addresses = new List<GuestAddress>();
        foreach (var device in json.EnumerateArray())
        {
            var name = ProxmoxCommands.String(device, "name") ?? "";
            if (name == "lo" || new[] { "docker", "br-", "veth", "virbr" }.Any(p => name.StartsWith(p, StringComparison.Ordinal))) continue;
            var mac = ProxmoxCommands.String(device, "hardware-address");
            var matching = mac is null ? [] : adapters.Where(a => Mac(a.MacAddress) == Mac(mac)).ToArray();
            if (matching.Length != 1 || !device.TryGetProperty("ip-addresses", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in ips.EnumerateArray())
            {
                if (GuestAddressRules.Parse(ProxmoxCommands.String(entry, "ip-address") ?? "") is not { } ip ||
                    IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv4MappedToIPv6) continue;
                var bytes = ip.GetAddressBytes();
                if (ip.AddressFamily == AddressFamily.InterNetwork && (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254)) continue;
                addresses.Add(new(ip.ToString(), ip.AddressFamily == AddressFamily.InterNetwork ? GuestAddressFamily.Ipv4 : GuestAddressFamily.Ipv6,
                    GuestAddressSource.GuestAgent, now, false, matching[0].AdapterId));
            }
        }
        return addresses.Distinct().ToArray();
    }
    public async Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string vmName, CancellationToken ct) => await (await CaptureAsync(ct)).GetReportedAddressesAsync(vmName, ct);
    public async Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string vmName, CancellationToken ct) => await (await CaptureAsync(ct)).GetAdaptersAsync(vmName, ct);
    public async Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct) => await (await CaptureAsync(ct)).GetNeighborsAsync(ct);
    public async Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct) => await (await CaptureAsync(ct)).GetGuestSubnetsAsync(ct);
    public async Task<IReadOnlyList<IPAddress>> GetHostAddressesAsync(CancellationToken ct) => await (await CaptureAsync(ct)).GetHostAddressesAsync(ct);

    private sealed class Captured : IGuestAddressProvider
    {
        internal readonly Dictionary<string, IReadOnlyList<GuestAddress>> Reported = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, IReadOnlyList<GuestAdapter>> Adapters = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<IPAddress> Host = [];
        internal readonly List<HostNeighbor> Neighbors = [];
        internal readonly List<GuestSubnet> Subnets = [];
        public Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string name, CancellationToken ct) => Task.FromResult(Reported.GetValueOrDefault(name) ?? []);
        public Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string name, CancellationToken ct) => Task.FromResult(Adapters.GetValueOrDefault(name) ?? []);
        public Task<IReadOnlyList<IPAddress>> GetHostAddressesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IPAddress>>(Host);
        public Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostNeighbor>>(Neighbors);
        public Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<GuestSubnet>>(Subnets);
    }
}
