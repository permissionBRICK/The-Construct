using System.Runtime.Versioning;

namespace Construct.Companion.Windows;

// S1 quit transport only. HTTP activation and installer quit are supplied by S2 IPC.
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private readonly EventWaitHandle quit = new(false, EventResetMode.AutoReset, @"Local\ConstructCompanion.Quit");
    private readonly Mutex mutex;
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        mutex = new Mutex(true, @"Local\ConstructCompanion", out var created);
        IsPrimary = created;
    }
    public void RequestQuit() => quit.Set();
    public bool QuitRequested() => quit.WaitOne(0);
    public void Dispose()
    {
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose(); quit.Dispose();
    }
}
