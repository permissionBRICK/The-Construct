using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Windows.Internal;

namespace Constructd.Windows.HyperV;

/// <summary>Read-only all-VM inventory. Child output and exception text never leave this boundary.</summary>
public sealed class HyperVInventory(IProcessRunner runner, ConstructdOptions options, IClock clock, IMediaStore? media = null) : IHypervisorInventory
{
    private long _epoch;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public Task<InventorySnapshot> ReadAsync(CancellationToken ct) => ReadAsync([], ct);
    public async Task<InventorySnapshot> ReadAsync(IReadOnlyList<Reservation> reservations, CancellationToken ct)
    {
        var epoch = Interlocked.Increment(ref _epoch);
        try
        {
            var artifacts = new List<object>();
            foreach (var row in reservations.Where(r => r.Resource == ReservationResource.Storage))
            {
                string? path = null;
                if (row.Artifact?.StartsWith("disk:", StringComparison.OrdinalIgnoreCase) == true) path = row.Artifact[5..];
                else if (media is not null && row.Artifact is { } artifact)
                {
                    MediaItem? item = null;
                    if (artifact.StartsWith("media:", StringComparison.Ordinal)) item = await media.GetAsync(artifact[6..], ct);
                    else if (artifact.StartsWith("upload:", StringComparison.Ordinal) && await media.GetUploadAsync(artifact[7..], ct) is { } upload)
                        item = await media.GetAsync(upload.MediaId, ct);
                    if (item is not null) path = item.Path;
                }
                if (path is not null) ArgumentGuard.WindowsPath(path, "capacity artifact");
                artifacts.Add(new { row.Artifact, Path = path, row.Volume });
            }
            var root = ArgumentGuard.WindowsPath(options.ScriptsDir, "Constructd:ScriptsDir").TrimEnd('\\', '/');
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                try {
                    . {{PowerShellLiteral.Quote(root + @"\drivers\hyperv-local\HyperVLocal.ChildVm.ps1")}}
                    $inputData = [Console]::In.ReadToEnd() | ConvertFrom-Json
                    $value = Get-ConstructHostInventory -Artifacts @($inputData.artifacts)
                    @{ ok = $true; value = $value } | ConvertTo-Json -Compress -Depth 12
                } catch { '{"ok":false}' }
                """;
            var result = await runner.RunAsync(options.PowerShellPath,
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
                JsonSerializer.Serialize(new { artifacts }, Json), TimeSpan.FromMinutes(2), null, ct);
            if (!result.Succeeded) return Unavailable(epoch);
            using var envelope = JsonDocument.Parse(result.StandardOutput.Trim());
            if (!envelope.RootElement.GetProperty("ok").GetBoolean()) return Unavailable(epoch);
            var snapshot = envelope.RootElement.GetProperty("value").Deserialize<InventorySnapshot>(Json);
            if (snapshot is null || snapshot.Host is null || snapshot.Vms is null || snapshot.Host.Volumes is null || snapshot.Problems is null ||
                snapshot.Host.TotalRamBytes <= 0 || snapshot.Host.FreeRamBytes < 0 || snapshot.Host.LogicalCpus <= 0 ||
                snapshot.Host.Volumes.Any(v => v.TotalBytes < 0 || v.FreeBytes < 0) || snapshot.Vms.Any(v => v.Disks is null || v.MemoryAssignedBytes < 0 || v.MemoryStartupBytes < 0)) return Unavailable(epoch);
            return snapshot with { Epoch = epoch, ObservedAt = clock.UtcNow, Host = snapshot.Host with { ObservedAt = clock.UtcNow } };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Unavailable(epoch); }
    }
    private InventorySnapshot Unavailable(long epoch) => new(epoch, clock.UtcNow, new(0, 0, 0, [], clock.UtcNow), [], false, ["inventory-unavailable"]);
}
