using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Constructd.Api.Contracts;
using Constructd.Api.Endpoints;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.AspNetCore.Http.Features;

namespace Constructd.Api.Backups;

/// <summary>
/// One <c>PUT /vms/{name}/backups/{id}/content</c> (docs/plans/host-config-backups.md §3.5, §3.6): the payload
/// is streamed into <c>&lt;id&gt;.age.partial</c> while it is hashed (SHA-256) and counted, never buffered.
/// It stops at <c>maxBytes</c> (413 <c>backup-too-large</c>) or when the host's free space falls below the
/// reserve, re-checked every <see cref="BackupCoordinator.SpaceCheckBytes"/> (507 <c>host-disk-space</c>).
/// On success the file is renamed to <c>&lt;id&gt;.age</c> and the session completes: a host-requested one
/// records the success and rotation runs; a slot upload replaces the VM's previous slot content and
/// touches no schedule state. Any failure removes the partial file and closes the session as failed
/// (recorded as a failure only for a host-requested session). The caller registered the upload
/// with <see cref="BackupCoordinator.BeginUpload"/>; its token is cancelled when the session is deleted.
/// </summary>
internal sealed class BackupUpload(HttpContext http, Vm vm, ConfigBackup backup, CancellationTokenSource upload,
    BackupCoordinator backups, IClock clock)
{
    private const int BufferBytes = 1 << 20;
    private readonly IBackupStore _store = backups.Store;
    private readonly IBackupFiles _files = backups.Files;
    private long _received;

    private sealed record Failure(int Status, string Code, string Detail, string? Reason);

    public async Task<IResult> RunAsync()
    {
        var ct = http.RequestAborted;
        BackupsConfig host;
        long maxBytes;
        await using (await backups.LockAsync(vm.Owner, vm.Name, ct))
        {
            host = await backups.ConfigAsync(ct);
            if (backup.KeysJson is null)
                return await CloseLockedAsync(new(409, "backup-keys-missing", "The keys file must be uploaded before the payload.", "host-refused"));
            var stored = BackupCoordinator.StoredBytes(await _store.ListAsync(vm.Owner, vm.Name, ct), backup.IsSlot);
            var free = _files.FreeBytes();
            if (free < host.MinFreeBytes)
                return await CloseLockedAsync(new(507, "host-disk-space", "The host is below its free-space reserve.", "host-refused"));
            maxBytes = backups.MaxBytes(host, backup.IsSlot, free, stored);
            if (http.Request.ContentLength is long announced && announced > maxBytes)
                return await CloseLockedAsync(TooLarge(maxBytes), MaxBytesExtra(maxBytes));
        }

        // This request alone may exceed the server's default body limit (Kestrel on both platforms).
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit) bodyLimit.MaxRequestBodySize = null;

        var (sha256, failure, error) = await StreamAsync(host, maxBytes, ct);
        if (failure is not null)
        {
            var result = await CloseAsync(failure, failure.Status == 413 ? MaxBytesExtra(maxBytes) : null);
            error?.Throw();
            return result;
        }

        await using (await backups.LockAsync(vm.Owner, vm.Name, CancellationToken.None))
        {
            if (await _store.GetAsync(backup.Id, CancellationToken.None) is not { IsOpen: true })
            {
                DeleteFiles();
                backups.EndUpload(backup.Id, upload);
                return BackupEndpoints.Fail(http, 409, "backup-not-open", "The session was closed during the upload.");
            }
            _files.Publish(vm.Owner, vm.Name, backup.Id);
            var completedAt = clock.UtcNow;
            // Only a host-requested pre-reinstall backup pins itself; a slot's trigger is always "slot".
            DateTimeOffset? pinned = !backup.IsSlot && backup.Trigger == BackupTriggers.PreReinstall ? backup.CreatedAt.AddDays(host.PinDays) : null;
            await _store.CompleteAsync(backup.Id, completedAt, _received, sha256!, pinned, CancellationToken.None);
            backups.EndUpload(backup.Id, upload);
            backups.Changes.Publish("backup.added", backup.Id, vm.Name, vm.Owner);
            if (backup.IsSlot)
            {
                // The new slot content is verified and complete: the previous one goes.
                foreach (var previous in (await _store.ListAsync(vm.Owner, vm.Name, CancellationToken.None))
                             .Where(b => b.IsSlot && b.IsComplete && b.Id != backup.Id).ToList())
                    await backups.DeleteLockedAsync(previous, CancellationToken.None);
            }
            else
            {
                var state = await _store.GetVmAsync(vm.Owner, vm.Name, CancellationToken.None) ?? new BackupVmState(vm.Owner, vm.Name);
                state = state with { LastSuccessAt = completedAt, FailingSince = null };
                await _store.SaveVmAsync(state, CancellationToken.None);
                // Old backups go only once the new one is complete.
                await backups.RotateLockedAsync(vm.Owner, vm.Name, host, state, CancellationToken.None);
            }
        }
        CodedProblems.Audit(http, "backup.upload", vm.Owner, target: backup.Id, extra: $"trigger={backup.Trigger}, sizeBytes={_received}");
        return TypedResults.Ok(new BackupUploadedResponse(backup.Id, _received, sha256!, backup.CreatedAt, BackupEndpoints.Kind(backup)));
    }

    private async Task<(string? Sha256, Failure? Failure, ExceptionDispatchInfo? Error)> StreamAsync(BackupsConfig host, long maxBytes, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            await using var output = _files.CreatePartial(vm.Owner, vm.Name, backup.Id);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var nextCheck = backups.SpaceCheckBytes;
            while (true)
            {
                int read;
                try { read = await http.Request.Body.ReadAsync(buffer.AsMemory(0, BufferBytes), upload.Token); }
                catch (Exception ex) when (ex is IOException && !upload.IsCancellationRequested)
                {
                    // A malformed or cut-off body; a server-side size limit counts as too large.
                    return (null, ex is BadHttpRequestException { StatusCode: 413 } ? TooLarge(maxBytes)
                        : new(ex is BadHttpRequestException bad ? bad.StatusCode : 400, "upload-failed", "The upload was interrupted.", "upload-failed"), null);
                }
                if (read == 0) break;
                _received += read;
                if (_received > maxBytes) return (null, TooLarge(maxBytes), null);
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), upload.Token);
                if (_received < nextCheck) continue;
                while (nextCheck <= _received) nextCheck += backups.SpaceCheckBytes;
                if (_files.FreeBytes() < host.MinFreeBytes)
                    return (null, new(507, "host-disk-space", "The host fell below its free-space reserve.", "host-refused"), null);
            }
            await output.FlushAsync(upload.Token);
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), null, null);
        }
        catch (OperationCanceledException) when (upload.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The session was deleted under the upload (by its owner or by cleanup): nothing to record.
            return (null, new(409, "backup-not-open", "The session was closed during the upload.", null), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, new(400, "upload-failed", "The client went away during the upload.", "upload-failed"), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A write that failed because the disk filled up is the host refusing; anything else is a server error.
            return SafeFreeBytes() < host.MinFreeBytes
                ? (null, new(507, "host-disk-space", "The host ran out of space.", "host-refused"), null)
                : (null, new(500, "upload-failed", "The upload could not be stored.", "upload-failed"), ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Failure TooLarge(long maxBytes) => new(413, "backup-too-large", $"The payload is larger than {maxBytes} bytes.", "host-refused");

    private static Dictionary<string, object?> MaxBytesExtra(long maxBytes) => new() { ["maxBytes"] = maxBytes };

    /// <summary>Closes the session as failed; the caller holds the pair's lock.</summary>
    private async Task<IResult> CloseLockedAsync(Failure failure, Dictionary<string, object?>? extra = null)
    {
        DeleteFiles();
        var current = await _store.GetAsync(backup.Id, CancellationToken.None);
        if (current is { IsOpen: true }) await _store.DeleteAsync(backup.Id, CancellationToken.None);
        backups.EndUpload(backup.Id, upload);
        // A session deleted under the upload was closed by someone else; its failure is not this upload's.
        // A slot upload's failure is not schedule state.
        if (failure.Reason is not null && current is not null && !backup.IsSlot)
            await backups.RecordFailureLockedAsync(vm.Owner, vm.Name, backup.Trigger, failure.Reason,
                failure.Reason == "host-refused" ? failure.Code : null, answersRequest: false, CancellationToken.None);
        try { _files.PruneDirectory(vm.Owner, vm.Name); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        http.AppendAuditDetail($"id={backup.Id}, receivedBytes={_received}");
        return BackupEndpoints.Fail(http, failure.Status, failure.Code, failure.Detail, extra);
    }

    private async Task<IResult> CloseAsync(Failure failure, Dictionary<string, object?>? extra)
    {
        await using (await backups.LockAsync(vm.Owner, vm.Name, CancellationToken.None))
            return await CloseLockedAsync(failure, extra);
    }

    private void DeleteFiles()
    {
        try { _files.Delete(vm.Owner, vm.Name, backup.Id); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private long SafeFreeBytes()
    {
        try { return _files.FreeBytes(); }
        catch (IOException) { return 0; }
    }
}
