using Constructd.Core.Abstractions;

namespace Constructd.Proxmox;

/// <summary>
/// Host self-update is not offered on a Proxmox node (re-running
/// <c>service/host/install-construct-host.sh</c> is the update path), so the recovery service that
/// looks for an interrupted update at startup finds nothing — quietly, rather than failing its check
/// every tick — and a launch is refused.
/// </summary>
public sealed class NoUpdaterLauncher : IUpdaterLauncher
{
    public Task LaunchAsync(UpdateHandoff handoff, CancellationToken ct) =>
        throw new NotSupportedException("Host self-update is not available on a Proxmox host; re-run install-construct-host.sh.");

    public Task<UpdateHandoff?> ReadHandoffAsync(CancellationToken ct) => Task.FromResult<UpdateHandoff?>(null);

    public Task<RecoveryRecord?> ReadRecoveryRecordAsync(CancellationToken ct) => Task.FromResult<RecoveryRecord?>(null);

    public Task<bool> TryWriteFenceAsync(UpdateFence fence, CancellationToken ct) => Task.FromResult(false);

    public Task<UpdateHandoff?> ReadOwnHandoffAsync(string expectedCommit, CancellationToken ct) => Task.FromResult<UpdateHandoff?>(null);
}
