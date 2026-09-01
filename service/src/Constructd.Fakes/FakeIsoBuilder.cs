using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>
/// Pretends to run <c>build-autoinstall-iso.sh</c> in WSL. Emits the same shape of progress the real
/// builder does so the SSE contract is exercised end to end.
/// </summary>
public sealed class FakeIsoBuilder : IIsoBuilder
{
    /// <summary>Set to make the build fail, so job failure handling can be tested.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every build this fake was asked for, in order.</summary>
    public List<string> Built { get; } = [];

    public Task<string> BuildAsync(
        string vmName,
        string seedUser,
        string seedPassword,
        string bootstrapPubKeyPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is not null)
        {
            return Task.FromException<string>(Failure);
        }

        progress?.Report($"building autoinstall ISO for {vmName} (seed user {seedUser})");
        lock (Built)
        {
            Built.Add(vmName);
        }

        var path = $"/fake/isos/{vmName}-autoinstall.iso";
        progress?.Report($"ISO ready: {path}");
        return Task.FromResult(path);
    }
}
