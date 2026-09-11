namespace Constructd.Api.Source;

/// <summary>Short registry/reader sections only. Never wait for a commit gate or do file I/O here.</summary>
public sealed class SourceCatalog
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    internal Dictionary<string, int> Readers { get; } = new(StringComparer.Ordinal);
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    { await mutex.WaitAsync(ct); return new Handle(mutex); }
    private sealed class Handle(SemaphoreSlim mutex) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) mutex.Release(); }
    }
}

/// <summary>The file handle closes before releasing its catalog reader, exactly once.</summary>
public sealed class SourceReadStream(Stream inner, Func<Task> release) : Stream
{
    private int disposed;
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        { try { inner.Dispose(); } finally { release().GetAwaiter().GetResult(); } }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        { try { await inner.DisposeAsync(); } finally { await release(); } }
        GC.SuppressFinalize(this);
    }
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
