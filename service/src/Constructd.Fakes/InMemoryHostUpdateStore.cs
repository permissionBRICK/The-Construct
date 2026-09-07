using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class InMemoryHostUpdateStore : IHostUpdateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostUpdateRecord> _rows = [];
    private static bool Active(HostUpdateRecord row) => row.State is HostUpdateState.Checking or HostUpdateState.Staged or HostUpdateState.Draining or HostUpdateState.HandedOff or HostUpdateState.Applying or HostUpdateState.RecoveryFailed or HostUpdateState.Interrupted;
    public Task<HostUpdateRecord?> GetAsync(string id, CancellationToken ct) { lock (_gate) return Task.FromResult(_rows.GetValueOrDefault(id)); }
    public Task<HostUpdateRecord?> GetActiveAsync(CancellationToken ct) { lock (_gate) return Task.FromResult(_rows.Values.FirstOrDefault(Active)); }
    public Task<IReadOnlyList<HostUpdateRecord>> ListAsync(int limit, CancellationToken ct) { lock (_gate) return Task.FromResult<IReadOnlyList<HostUpdateRecord>>(_rows.Values.OrderByDescending(r => r.Started).Take(Math.Max(0, limit)).ToArray()); }
    public Task<bool> TryStartAsync(HostUpdateRecord record, CancellationToken ct)
    { lock (_gate) { if (_rows.ContainsKey(record.Id) || _rows.Values.Any(Active)) return Task.FromResult(false); _rows.Add(record.Id, record); return Task.FromResult(true); } }
    public Task UpsertAsync(HostUpdateRecord record, CancellationToken ct)
    { lock (_gate) { if (Active(record) && _rows.Values.Any(r => r.Id != record.Id && Active(r))) throw new InvalidOperationException("An update is already active."); _rows[record.Id] = record; return Task.CompletedTask; } }
}
