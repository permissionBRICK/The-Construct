namespace Constructd.Core.Services;

/// <summary>Transfers an HTTP admission to a legacy SubmitAsync job without a check/enqueue gap.</summary>
public sealed class MaintenanceAdmission(IDisposable handle, string kind) : IDisposable
{
    public static AsyncLocal<MaintenanceAdmission?> Current { get; } = new();
    private int _references = 1;
    public IDisposable? Retain(string jobKind) { if (kind != jobKind) return null; Interlocked.Increment(ref _references); return new Reference(this); }
    public void Dispose() { if (Interlocked.Decrement(ref _references) == 0) handle.Dispose(); }
    private sealed class Reference(MaintenanceAdmission owner) : IDisposable
    { private MaintenanceAdmission? _owner = owner; public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose(); }
}
