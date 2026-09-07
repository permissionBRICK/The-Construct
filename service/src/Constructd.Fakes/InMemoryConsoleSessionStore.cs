using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed class InMemoryConsoleSessionStore : IConsoleSessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ConsoleSession> _sessions = new();
    private readonly Dictionary<(string Id, string Bucket), (long Second, int Count)> _rates = new();
    public ConsoleSession? TryCreate(string vmName, string principal, int nativeWidth, int nativeHeight, TimeSpan ttl, DateTimeOffset now)
    {
        lock (_gate)
        {
            RemoveExpired(now);
            if (_sessions.Values.Count(s => Ownership.SameName(s.VmName, vmName)) >= 4) return null;
            var session = new ConsoleSession(Guid.NewGuid().ToString("n"), vmName, principal, now, now + ttl, nativeWidth, nativeHeight);
            _sessions.Add(session.Id, session); return session;
        }
    }
    public ConsoleSession? Get(string id, DateTimeOffset now)
    { lock (_gate) { if (!_sessions.TryGetValue(id, out var s)) return null; if (s.ExpiresAt <= now) { Remove(id); return null; } return s; } }
    public ConsoleSession? Renew(string id, TimeSpan ttl, DateTimeOffset now, int? nativeWidth = null, int? nativeHeight = null)
    { lock (_gate) { var s = Get(id, now); if (s is null) return null; return _sessions[id] = s with { ExpiresAt = now + ttl, NativeWidth = nativeWidth ?? s.NativeWidth, NativeHeight = nativeHeight ?? s.NativeHeight }; } }
    public bool Remove(string id)
    { lock (_gate) { foreach (var k in _rates.Keys.Where(k => k.Id == id).ToArray()) _rates.Remove(k); return _sessions.Remove(id); } }
    private int RemoveWhere(Func<ConsoleSession, bool> predicate)
    { lock (_gate) { var ids = _sessions.Values.Where(predicate).Select(s => s.Id).ToArray(); foreach (var id in ids) Remove(id); return ids.Length; } }
    public int RemoveExpired(DateTimeOffset now) => RemoveWhere(s => s.ExpiresAt <= now);
    public int RemoveForVm(string vmName) => RemoveWhere(s => Ownership.SameName(s.VmName, vmName));
    public int RemoveForVmExcept(string vmName, IReadOnlyList<string> principals) => RemoveWhere(s => Ownership.SameName(s.VmName, vmName) && !principals.Contains(s.Principal, Ownership.NameComparer));
    public int RemoveForPrincipal(string principal) => RemoveWhere(s => Ownership.SameName(s.Principal, principal));
    public bool TryTakeRate(string id, string bucket, int perSecond, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (Get(id, now) is null || perSecond < 1) return false;
            var key = (id, bucket); var old = _rates.GetValueOrDefault(key); var second = now.ToUnixTimeSeconds();
            var count = old.Second == second ? old.Count : 0; if (count >= perSecond) return false;
            _rates[key] = (second, count + 1); return true;
        }
    }
}
