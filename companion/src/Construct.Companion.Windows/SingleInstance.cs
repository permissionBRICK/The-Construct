using System.Runtime.Versioning;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex=new(false,@"Local\ConstructCompanion");
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; } // the previous owner died holding it: we own it now
    }
    public void Dispose() { if (IsPrimary) mutex.ReleaseMutex(); mutex.Dispose(); }
}
