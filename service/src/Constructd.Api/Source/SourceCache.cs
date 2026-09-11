using System.Security.Cryptography;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
namespace Constructd.Api.Source;

public sealed record SourceOpen(SourceItem Item, SourceReadStream Stream);
public sealed record SourceRetained(string Commit, string Reason);
public sealed record SourceCleanupResult(IReadOnlyList<string> Removed, IReadOnlyList<SourceRetained> Retained);
public interface ISourceCache
{
    Task<SourceItem> EnsureAsync(string commit, IProgress<string>? progress, CancellationToken ct, string? jobId = null);
    Task<SourceOpen> OpenAsync(string commit, CancellationToken ct);
    Task<SourceItem?> ReadyAsync(string commit, CancellationToken ct);
    Task RequestDeleteAsync(string commit, bool force, CancellationToken ct);
    Task<SourceCleanupResult> PruneAsync(string initiator, IProgress<string>? progress, CancellationToken ct, string? commit = null);
    Task RecoverAsync(CancellationToken ct);
}

public sealed class SourceCache(ISourceStore store, ISourceFiles files, IReleaseSource releases, SourceGate gates,
    SourceCatalog catalog, ConstructdOptions options, IHostConfigStore config, IClock clock, IAuditLog audit) : ISourceCache
{
    private HostAdminSourceOptions Limits => options.HostAdmin.Source;
    private static void Validate(string commit) { if (!SourceZipRules.ValidCommit(commit)) throw new SourceException("validation"); }
    private Task Audit(string action, string target, string detail, bool failed = false, string actor = "system") =>
        audit.AppendAsync(new(clock.UtcNow, actor, action, target, failed ? AuditOutcome.Failure : AuditOutcome.Success, detail), CancellationToken.None);
    public async Task<SourceItem?> ReadyAsync(string commit, CancellationToken ct)
    {
        Validate(commit);
        using (await catalog.AcquireAsync(ct))
        {
            var item = await store.GetAsync(commit, ct);
            if (item?.State != SourceState.Ready) return null;
            await store.TouchAsync(commit, clock.UtcNow, ct); return item;
        }
    }
    public async Task<SourceItem> EnsureAsync(string commit, IProgress<string>? progress, CancellationToken ct, string? jobId = null)
    {
        Validate(commit);
        var acquire = gates.AcquireAsync(commit, ct);
        if (!acquire.IsCompleted) progress?.Report("waiting for another download of " + commit[..7]);
        using var gate = await acquire;
        var reserved = false;
        try
        {
            SourceItem? item;
            using (await catalog.AcquireAsync(ct))
            {
                item = await store.GetAsync(commit, ct);
                if (item?.State == SourceState.Ready) { await store.TouchAsync(commit, clock.UtcNow, ct); return item; }
            }
            if (item?.State == SourceState.Deleting && await RemoveLockedAsync(commit) != "removed") throw new SourceException("source-cleanup-pending");
            // A downloading row only survives here after recovery was skipped or interrupted.
            if (item?.State == SourceState.Downloading) throw new SourceException("source-cleanup-pending");
            progress?.Report("check");
            var settings = HostUpdateTrust.Apply(await config.GetAsync<UpdatesConfig>("updates", ct) ?? HostAdminDefaults.Updates, options);
            var asset = await releases.GetSourceAssetAsync(settings.Repository, commit, ct);
            if (asset.SizeBytes <= 0 || asset.Commit != commit) throw new SourceException("release-source-invalid-metadata");
            if (asset.SizeBytes > Limits.MaxItemBytes) throw new SourceException("source-too-large");
            if (files.FreeBytes() < checked(asset.SizeBytes + (1L << 30))) throw new SourceException("insufficient-space");
            item = new(commit, SourceState.Downloading, asset.SizeBytes, asset.Sha256, asset.Tag, null, jobId, clock.UtcNow, null, clock.UtcNow);
            using (await catalog.AcquireAsync(ct))
            {
                if (await store.CommittedBytesAsync(ct) > Limits.MaxTotalBytes - asset.SizeBytes) throw new SourceException("source-cache-full");
                await store.UpsertAsync(item, ct); reserved = true;
            }
            progress?.Report("download");
            try
            {
                await releases.DownloadAsync(new("construct-source-" + commit + ".zip", asset.Url, asset.SizeBytes),
                    files.PathFor(commit, SourceFileKind.Part), progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UpdateException)
            { throw new SourceException("source-transfer-failed"); }
            progress?.Report("verify");
            await using (var input = files.OpenRead(commit, SourceFileKind.Part))
            {
                if (input.Length != item.SizeBytes) throw new SourceException("source-transfer-failed");
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) != item.Sha256) throw new SourceException("source-hash-mismatch");
                input.Position = 0; SourceZipRules.Validate(input);
            }
            files.Publish(commit);
            item = item with { State = SourceState.Ready, ReadyAt = clock.UtcNow, LastUsedAt = clock.UtcNow };
            using (await catalog.AcquireAsync(ct)) await store.UpsertAsync(item, ct);
            progress?.Report("ready"); await Audit("source.fetch.completed", commit, $"job={jobId}, bytes={item.SizeBytes}");
            return item;
        }
        catch (Exception ex)
        {
            var code = ex switch { SourceException s => s.Code, UpdateException u => u.Code, OperationCanceledException => "cancelled", _ => "source-transfer-failed" };
            if (reserved)
            {
                using (await catalog.AcquireAsync(CancellationToken.None))
                {
                    var current = await store.GetAsync(commit, CancellationToken.None);
                    if (current is not null) await store.UpsertAsync(current with { State = SourceState.Deleting, Error = code }, CancellationToken.None);
                }
                await RemoveLockedAsync(commit);
            }
            await Audit("source.fetch.failed", commit, "code=" + code, true);
            throw new SourceException(code);
        }
    }
    public async Task<SourceOpen> OpenAsync(string commit, CancellationToken ct)
    {
        Validate(commit); SourceItem item;
        using (await catalog.AcquireAsync(ct))
        {
            item = await store.GetAsync(commit, ct) ?? throw new SourceException("source-not-cached");
            if (item.State != SourceState.Ready) throw new SourceException(item.State == SourceState.Downloading ? "source-downloading" : "source-not-cached");
            catalog.Readers[commit] = catalog.Readers.GetValueOrDefault(commit) + 1;
        }
        Stream? input = null; string reason = "missing";
        try
        {
            input = files.OpenRead(commit, SourceFileKind.Zip); reason = "size";
            if (input.Length != item.SizeBytes) throw new SourceException("source-corrupt");
            reason = "hash";
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) != item.Sha256) throw new SourceException("source-corrupt");
            input.Position = 0;
            using (await catalog.AcquireAsync(ct)) await store.TouchAsync(commit, clock.UtcNow, ct);
            return new(item, new SourceReadStream(input, () => ReleaseReaderAsync(commit)));
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                using (await catalog.AcquireAsync(CancellationToken.None))
                {
                    var current = await store.GetAsync(commit, CancellationToken.None);
                    if (current?.State == SourceState.Ready) await store.UpsertAsync(current with { State = SourceState.Deleting, Error = "corrupt" }, CancellationToken.None);
                }
            }
            try { if (input is not null) await input.DisposeAsync(); } finally { await ReleaseReaderAsync(commit); }
            if (ex is OperationCanceledException) throw;
            await Audit("source.corrupt", commit, "reason=" + reason, true);
            throw new SourceException("source-corrupt");
        }
    }
    private async Task ReleaseReaderAsync(string commit)
    {
        bool remove;
        using (await catalog.AcquireAsync(CancellationToken.None))
        {
            var count = catalog.Readers[commit] - 1;
            if (count == 0) catalog.Readers.Remove(commit); else catalog.Readers[commit] = count;
            remove = count == 0 && (await store.GetAsync(commit, CancellationToken.None))?.State == SourceState.Deleting;
        }
        if (remove)
        {
            using var gate = await gates.TryAcquireAsync(commit, CancellationToken.None);
            if (gate is not null) await RemoveLockedAsync(commit);
        }
    }
    // Caller owns this commit's gate. No catalog lock covers the physical deletion.
    private async Task<string> RemoveLockedAsync(string commit)
    {
        SourceItem? item;
        using (await catalog.AcquireAsync(CancellationToken.None))
        {
            item = await store.GetAsync(commit, CancellationToken.None);
            if (item?.State != SourceState.Deleting) return item?.State == SourceState.Failed ? "removed" : "ready";
            if (catalog.Readers.GetValueOrDefault(commit) > 0) return "busy";
        }
        string? error = null;
        try
        {
            files.Delete(commit, SourceFileKind.Part); files.Delete(commit, SourceFileKind.Zip);
            if (files.Exists(commit, SourceFileKind.Part) || files.Exists(commit, SourceFileKind.Zip)) error = "io";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SourceException) { error = "io"; }
        using (await catalog.AcquireAsync(CancellationToken.None))
        {
            await store.UpsertAsync(item with { State = error is null ? SourceState.Failed : SourceState.Deleting,
                Error = error is null ? item.Error : (item.Error ?? "admin").Split("; cleanup:")[0] + "; cleanup:" + error,
                LastUsedAt = clock.UtcNow }, CancellationToken.None);
        }
        if (error is null) await Audit("source.removed", commit, $"reason={item.Error}, bytes={item.SizeBytes}");
        return error is null ? "removed" : "cleanup-failed:" + error;
    }
    public async Task RequestDeleteAsync(string commit, bool force, CancellationToken ct)
    {
        Validate(commit);
        using (await catalog.AcquireAsync(ct))
        {
            var item = await store.GetAsync(commit, ct);
            if (item is null || item.State == SourceState.Failed) throw new SourceException("source-not-cached");
            if (item.State == SourceState.Downloading) throw new SourceException("source-in-use");
            if (!force && (await store.ListPinnedCommitsAsync(ct)).Any(p => SourceZipRules.Pinned(commit, p))) throw new SourceException("source-pinned");
            await store.UpsertAsync(item with { State = SourceState.Deleting, Error = "admin" }, ct);
        }
    }
    public async Task<SourceCleanupResult> PruneAsync(string initiator, IProgress<string>? progress, CancellationToken ct, string? commit = null)
    {
        IReadOnlyList<SourceItem> items;
        using (await catalog.AcquireAsync(ct)) items = await store.ListAsync(ct);
        var removed = new List<string>(); var retained = new List<SourceRetained>();
        foreach (var snapshot in items.Where(i => commit is null || i.Commit == commit))
        {
            if (snapshot.State == SourceState.Ready) { retained.Add(new(snapshot.Commit, "ready")); continue; }
            using var gate = await gates.TryAcquireAsync(snapshot.Commit, ct);
            if (gate is null) { retained.Add(new(snapshot.Commit, "busy")); continue; }
            SourceItem? item;
            using (await catalog.AcquireAsync(ct)) item = await store.GetAsync(snapshot.Commit, ct);
            if (item is null) continue;
            if (item.State == SourceState.Deleting)
            {
                var result = await RemoveLockedAsync(item.Commit);
                if (result == "removed") removed.Add(item.Commit); else retained.Add(new(item.Commit, result));
            }
            else if (item.State == SourceState.Failed && item.LastUsedAt < clock.UtcNow.AddHours(-24))
            { using (await catalog.AcquireAsync(ct)) await store.DeleteAsync(item.Commit, ct); removed.Add(item.Commit); }
            else retained.Add(new(item.Commit, item.State == SourceState.Ready ? "ready" : "busy"));
        }
        foreach (var orphan in files.List().Where(f => (commit is null || f.Commit == commit) && f.Modified < clock.UtcNow.AddHours(-1)))
        {
            using var gate = await gates.TryAcquireAsync(orphan.Commit, ct);
            if (gate is null) continue;
            using (await catalog.AcquireAsync(ct)) if (await store.GetAsync(orphan.Commit, ct) is not null) continue;
            try { files.Delete(orphan.Commit, orphan.Kind); if (!files.Exists(orphan.Commit, orphan.Kind)) removed.Add(orphan.Commit); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SourceException) { retained.Add(new(orphan.Commit, "cleanup-failed:io")); }
        }
        var resultSet = new SourceCleanupResult(removed.Distinct().ToArray(), retained);
        await Audit("host.source.cleanup", commit ?? "", $"removed={resultSet.Removed.Count}, retained={retained.Count}", actor: initiator);
        return resultSet;
    }
    public async Task RecoverAsync(CancellationToken ct)
    {
        IReadOnlyList<SourceItem> items;
        using (await catalog.AcquireAsync(ct)) items = await store.ListAsync(ct);
        var interrupted = 0; var missing = 0; var cleaned = 0; var pending = 0;
        foreach (var item in items)
        {
            using var gate = await gates.AcquireAsync(item.Commit, ct);
            var absent = item.State == SourceState.Ready && !files.Exists(item.Commit, SourceFileKind.Zip);
            if (item.State == SourceState.Downloading || absent)
            {
                if (absent) { missing++; await Audit("source.corrupt", item.Commit, "reason=missing", true); } else interrupted++;
                using (await catalog.AcquireAsync(ct)) await store.UpsertAsync(item with { State = SourceState.Deleting, Error = absent ? "missing" : "interrupted" }, ct);
            }
            if (item.State is SourceState.Downloading or SourceState.Deleting || absent)
            { if (await RemoveLockedAsync(item.Commit) == "removed") cleaned++; else pending++; }
        }
        if (interrupted + missing + cleaned + pending > 0)
            await Audit("source.recover", "", $"interrupted={interrupted}, missing={missing}, cleaned={cleaned}, pending={pending}");
    }
}
