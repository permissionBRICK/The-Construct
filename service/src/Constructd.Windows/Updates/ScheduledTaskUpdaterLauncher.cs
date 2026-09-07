using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
namespace Constructd.Windows.Updates;

public sealed class ScheduledTaskUpdaterLauncher(IProcessRunner runner, IHostLock hostLock, string dataDir) : IUpdaterLauncher
{
    public string Root => Path.Combine(dataDir, "updates");
    public async Task PrepareAsync(UpdateHandoff handoff, CancellationToken ct)
    {
        // The service holds updater.lock for a fresh re-apply of a closed pre-replacement attempt.
        var fence=await ReadFenceAsync(ct);
        if(fence?.UpdateId==handoff.UpdateId && fence.Disposition==FenceDisposition.Closed)
        {
            var record=await ReadRecoveryRecordAsync(ct);
            if(record?.UpdateId==handoff.UpdateId && record.ReplaceStarted) throw new UpdateException("update-not-interrupted");
            var backup=Path.Combine(Root,"backup-"+handoff.UpdateId);UpdateFiles.NoLinks(backup);
            if(Directory.Exists(backup)) Directory.Delete(backup,true);
            if(record?.UpdateId==handoff.UpdateId) File.Delete(Path.Combine(Root,"last-update.json"));
        }
        await UpdateFiles.WriteAsync(Path.Combine(Root,"handoff.json"),handoff,ct);
        if(fence?.UpdateId==handoff.UpdateId && fence.Disposition==FenceDisposition.Closed) File.Delete(Path.Combine(Root,"fence.json"));
    }
    public async Task LaunchAsync(UpdateHandoff handoff, CancellationToken ct)
    {
        await PrepareAsync(handoff, ct);
        await LaunchTaskAsync(handoff, false, ct);
    }
    public async Task LaunchTaskAsync(UpdateHandoff handoff, bool resume, CancellationToken ct)
    {
        static string Quote(string value)
        {
            if (value.Any(c => c < 32 || c == '"')) throw new UpdateException("invalid-update-path");
            return "\"" + value.TrimEnd('\\') + "\"";
        }
        var command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
            Quote(Path.Combine(handoff.StagedPath, "extracted", "updater", "Update-ConstructHost.ps1")) + " -Handoff " +
            Quote(Path.Combine(Root, "handoff.json")) + (resume ? " -Resume" : "");
        // schtasks /TR is a documented executable command-line argument, not shell source.
        var create = await runner.RunAsync("schtasks.exe", ["/Create", "/TN", "Construct-HostUpdate", "/SC", "ONCE", "/ST",
            DateTime.Now.AddMinutes(1).ToString("HH:mm"), "/RU", "SYSTEM", "/RL", "HIGHEST", "/F", "/TR", command], null, TimeSpan.FromSeconds(20), null, ct);
        if (!create.Succeeded) throw new UpdateException("updater-launch-failed");
        var run = await runner.RunAsync("schtasks.exe", ["/Run", "/TN", "Construct-HostUpdate"], null, TimeSpan.FromSeconds(20), null, ct);
        if (!run.Succeeded)
        {
            await runner.RunAsync("schtasks.exe", ["/Delete", "/TN", "Construct-HostUpdate", "/F"], null, TimeSpan.FromSeconds(20), null, CancellationToken.None);
            throw new UpdateException("updater-launch-failed");
        }
    }
    public Task ResumeAsync(UpdateHandoff handoff, CancellationToken ct) => LaunchTaskAsync(handoff, true, ct);
    public Task<UpdateFence?> ReadFenceAsync(CancellationToken ct) => UpdateFiles.ReadAsync<UpdateFence>(Path.Combine(Root,"fence.json"),ct);
    public Task<UpdateHandoff?> ReadHandoffAsync(CancellationToken ct) => UpdateFiles.ReadAsync<UpdateHandoff>(Path.Combine(Root,"handoff.json"),ct);
    public Task<RecoveryRecord?> ReadRecoveryRecordAsync(CancellationToken ct) => UpdateFiles.ReadAsync<RecoveryRecord>(Path.Combine(Root,"last-update.json"),ct);
    public async Task<bool> TryWriteFenceAsync(UpdateFence fence, CancellationToken ct)
    {
        await using var held = await hostLock.TryAcquireAsync("updater.lock", TimeSpan.Zero, ct);
        if (held is null) return false;
        var record=await ReadRecoveryRecordAsync(ct);
        if(fence.Disposition==FenceDisposition.Closed && record?.UpdateId==fence.UpdateId && record.ReplaceStarted) return false;
        await UpdateFiles.WriteAsync(Path.Combine(Root,"fence.json"),fence,ct); return true;
    }
    public async Task<UpdateHandoff?> ReadOwnHandoffAsync(string expectedCommit, CancellationToken ct)
    { var handoff = await ReadHandoffAsync(ct); return handoff?.Commit == expectedCommit || handoff?.PreviousCommit == expectedCommit ? handoff : null; }
}
