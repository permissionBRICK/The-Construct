using Constructd.Core.Abstractions;
namespace Constructd.Windows.Updates;

public sealed class FileHostLock(string dataDir) : IHostLock
{
    private string FilePath(string name) => name switch {
        "admin.lock" => Path.Combine(dataDir, name),
        "updater.lock" => Path.Combine(dataDir, "updates", name),
        _ => throw new ArgumentException("Unknown host lock.") };
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken ct)
    {
        var path = FilePath(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            ct.ThrowIfCancellationRequested(); UpdateFiles.NoLinks(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { if (elapsed.Elapsed >= wait) return null; }
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        } while (true);
    }
    public bool IsHeldByAnotherProcess(string name)
    {
        var path = FilePath(name); if (!File.Exists(path)) return false;
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
}
