using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;
namespace Constructd.Windows.Network;

/// <summary>One PowerShell invocation captures all managed VM and host facts for a pass. KVP is untrusted.</summary>
public sealed class HyperVGuestAddressProvider(IProcessRunner runner, ConstructdOptions options,
    IVmRepository vms, IClock clock, ILogger<HyperVGuestAddressProvider>? logger = null) : IGuestAddressProvider, IGuestAddressSnapshotProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public sealed record VmSnapshot(string Name, IReadOnlyList<GuestAddress> Addresses, IReadOnlyList<GuestAdapter> Adapters, string? Error = null);
    public sealed record Snapshot(IReadOnlyList<VmSnapshot> Vms, IReadOnlyList<HostNeighbor> Neighbors,
        IReadOnlyList<GuestSubnet> Subnets, IReadOnlyList<string> HostAddresses);

    public async Task<IGuestAddressProvider> CaptureAsync(CancellationToken ct)
    {
        try
        {
            var managed = (await vms.ListAsync(null, ct)).Where(v => !v.Deleting && v.State != VmState.Absent && (v.Kind == VmKind.Primary || v.Incarnation is not null)).ToArray();
            var root = ArgumentGuard.WindowsPath(options.ScriptsDir, "Constructd:ScriptsDir").TrimEnd('\\', '/');
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                try {
                    . {{PowerShellLiteral.Quote(root + @"\drivers\hyperv-local\HyperVLocal.ChildVm.ps1")}}
                    $data = [Console]::In.ReadToEnd() | ConvertFrom-Json
                    $value = Get-ConstructVmAddresses -VmRequests @($data.vms)
                    @{ ok = $true; value = $value } | ConvertTo-Json -Compress -Depth 12
                } catch { '{"ok":false}' }
                """;
            var result = await runner.RunAsync(options.PowerShellPath,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                JsonSerializer.Serialize(new { vms = managed.Select(vm => new { name = vm.Name, vmId = vm.Incarnation }) }, Json), TimeSpan.FromSeconds(30), null, ct);
            if (!result.Succeeded)
            {
                logger?.LogWarning("Guest network snapshot process failed (exit code {ExitCode}); guest addresses are unavailable.", result.ExitCode);
                return Empty();
            }
            using var doc = JsonDocument.Parse(result.StandardOutput.Trim());
            if (!doc.RootElement.GetProperty("ok").GetBoolean()) return Failed("script-failed");
            var snapshot = doc.RootElement.GetProperty("value").Deserialize<Snapshot>(Json);
            if (snapshot?.Vms is null || snapshot.Vms.Any(v => v.Name is null || v.Addresses is null || v.Adapters is null) ||
                snapshot.Neighbors is null || snapshot.Subnets is null || snapshot.HostAddresses is null) return Failed("invalid-snapshot");
            foreach (var vm in snapshot.Vms.Where(v => v.Error is not null))
            {
                var name = managed.FirstOrDefault(m => StringComparer.OrdinalIgnoreCase.Equals(m.Name, vm.Name))?.Name ?? "unknown";
                var category = vm.Error is "vm-not-found" or "vm-incarnation-changed" or "adapter-query-failed" ? vm.Error : "vm-query-failed";
                logger?.LogWarning("Guest network snapshot unavailable for VM {VmName} ({Category}).", name, category);
            }
            return new Captured(snapshot with { Vms = snapshot.Vms.Select(v => v.Error is not null
                ? v with { Addresses = [], Adapters = [] }
                : v with { Addresses = v.Addresses.Select(a => a with { Verified = false, ObservedAt = clock.UtcNow }).ToArray() }).ToArray() });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Failed(ex.GetType().Name); } // Type only: never exception/output text from a guest or process.
    }
    private IGuestAddressProvider Failed(string category)
    {
        logger?.LogWarning("Guest network snapshot failed ({Category}); guest addresses are unavailable.", category);
        return Empty();
    }
    private static IGuestAddressProvider Empty() => new Captured(new([], [], [], []));
    public async Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string vmName, CancellationToken ct) => await (await CaptureAsync(ct)).GetReportedAddressesAsync(vmName, ct);
    public async Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string vmName, CancellationToken ct) => await (await CaptureAsync(ct)).GetAdaptersAsync(vmName, ct);
    public async Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct) => await (await CaptureAsync(ct)).GetNeighborsAsync(ct);
    public async Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct) => await (await CaptureAsync(ct)).GetGuestSubnetsAsync(ct);
    public async Task<IReadOnlyList<IPAddress>> GetHostAddressesAsync(CancellationToken ct) => await (await CaptureAsync(ct)).GetHostAddressesAsync(ct);

    private sealed class Captured(Snapshot snapshot) : IGuestAddressProvider
    {
        public Task<IReadOnlyList<GuestAddress>> GetReportedAddressesAsync(string vmName, CancellationToken ct) =>
            Task.FromResult(snapshot.Vms.FirstOrDefault(v => StringComparer.OrdinalIgnoreCase.Equals(v.Name, vmName))?.Addresses ?? (IReadOnlyList<GuestAddress>)[]);
        public Task<IReadOnlyList<GuestAdapter>> GetAdaptersAsync(string vmName, CancellationToken ct) =>
            Task.FromResult(snapshot.Vms.FirstOrDefault(v => StringComparer.OrdinalIgnoreCase.Equals(v.Name, vmName))?.Adapters ?? (IReadOnlyList<GuestAdapter>)[]);
        public Task<IReadOnlyList<HostNeighbor>> GetNeighborsAsync(CancellationToken ct) => Task.FromResult(snapshot.Neighbors);
        public Task<IReadOnlyList<GuestSubnet>> GetGuestSubnetsAsync(CancellationToken ct) => Task.FromResult(snapshot.Subnets);
        public Task<IReadOnlyList<IPAddress>> GetHostAddressesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IPAddress>>(
            snapshot.HostAddresses.Select(Core.Logic.GuestAddressRules.Parse).OfType<IPAddress>().ToArray());
    }
}
