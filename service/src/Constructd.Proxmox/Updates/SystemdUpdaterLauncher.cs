using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Windows.Updates;

namespace Constructd.Proxmox.Updates;

public sealed class SystemdUpdaterLauncher(IProcessRunner runner, IHostLock hostLock, string dataDir) : IUpdaterLauncher
{
    public string Root => Path.Combine(dataDir, "updates");

    private void Validate(UpdateHandoff h)
    {
        if (!Regex.IsMatch(h.UpdateId, "\\A[a-f0-9]{32}\\z")) throw new UpdateException("invalid-update-path");
        foreach (var path in new[] { Root, h.StagedPath, h.PublishDir, h.ScriptsDir, h.DataDir, h.AdminCliPath })
        {
            if (!Path.IsPathFullyQualified(path) || path.Any(c => char.IsControl(c) || c is '\"' or '\''))
                throw new UpdateException("invalid-update-path");
            UpdateFiles.NoLinks(path);
        }
        if (Path.GetFullPath(h.DataDir) != Path.GetFullPath(dataDir)) throw new UpdateException("invalid-update-path");
    }

    public async Task PrepareAsync(UpdateHandoff handoff, CancellationToken ct)
    {
        Validate(handoff);
        // The caller holds updater.lock while retiring a closed pre-replacement attempt.
        var fence = await ReadFenceAsync(ct);
        if (fence?.UpdateId == handoff.UpdateId && fence.Disposition == FenceDisposition.Closed)
        {
            var record = await ReadRecoveryRecordAsync(ct);
            if (record?.UpdateId == handoff.UpdateId && record.ReplaceStarted) throw new UpdateException("update-not-interrupted");
            var backup = Path.Combine(Root, "backup-" + handoff.UpdateId); UpdateFiles.NoLinks(backup);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            if (record?.UpdateId == handoff.UpdateId) File.Delete(Path.Combine(Root, "last-update.json"));
        }
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, "handoff.json"); UpdateFiles.NoLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(temp, options))
            {
                await JsonSerializer.SerializeAsync(file, handoff, UpdateFiles.Json, ct);
                file.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (fence?.UpdateId == handoff.UpdateId && fence.Disposition == FenceDisposition.Closed) File.Delete(Path.Combine(Root, "fence.json"));
    }

    public async Task LaunchAsync(UpdateHandoff handoff, CancellationToken ct)
    {
        await PrepareAsync(handoff, ct);
        await LaunchUnitAsync(handoff, false, ct);
    }

    private async Task LaunchUnitAsync(UpdateHandoff handoff, bool resume, CancellationToken ct)
    {
        Validate(handoff);
        List<string> args = ["--unit", "construct-host-update-" + handoff.UpdateId, "--collect", "--quiet",
            "--property=KillMode=process", "/bin/bash", Path.Combine(handoff.StagedPath, "extracted", "updater", "update-construct-host.sh"),
            "--handoff", Path.Combine(Root, "handoff.json")];
        if (resume) args.Add("--resume");
        try
        {
            var result = await runner.RunAsync("systemd-run", args, null, TimeSpan.FromSeconds(20), null, ct);
            if (!result.Succeeded) throw new UpdateException("updater-launch-failed");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        { throw new UpdateException("updater-launch-failed"); }
    }

    public Task ResumeAsync(UpdateHandoff handoff, CancellationToken ct) => LaunchUnitAsync(handoff, true, ct);
    public Task<UpdateFence?> ReadFenceAsync(CancellationToken ct) => UpdateFiles.ReadAsync<UpdateFence>(Path.Combine(Root, "fence.json"), ct);
    public Task<UpdateHandoff?> ReadHandoffAsync(CancellationToken ct) => UpdateFiles.ReadAsync<UpdateHandoff>(Path.Combine(Root, "handoff.json"), ct);
    public Task<RecoveryRecord?> ReadRecoveryRecordAsync(CancellationToken ct) => UpdateFiles.ReadAsync<RecoveryRecord>(Path.Combine(Root, "last-update.json"), ct);
    public async Task<bool> TryWriteFenceAsync(UpdateFence fence, CancellationToken ct)
    {
        await using var held = await hostLock.TryAcquireAsync("updater.lock", TimeSpan.Zero, ct);
        if (held is null) return false;
        var record = await ReadRecoveryRecordAsync(ct);
        if (fence.Disposition == FenceDisposition.Closed && record?.UpdateId == fence.UpdateId && record.ReplaceStarted) return false;
        await UpdateFiles.WriteAsync(Path.Combine(Root, "fence.json"), fence, ct);
        return true;
    }
    public async Task<UpdateHandoff?> ReadOwnHandoffAsync(string expectedCommit, CancellationToken ct)
    {
        var h = await ReadHandoffAsync(ct);
        return h?.Commit == expectedCommit || h?.PreviousCommit == expectedCommit ? h : null;
    }
}
