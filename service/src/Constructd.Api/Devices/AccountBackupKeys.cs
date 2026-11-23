using System.Security.Cryptography;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Sync.Participation;

namespace Constructd.Api.Devices;

/// <summary>
/// The account's backup recipient <c>B.pub</c> on its VMs (docs/plans/construct-platform-design.md 8.2,
/// 13.1). <c>B</c>'s private half stays on devices; its public half is registered here once by a device
/// (<c>PUT /device/backup-key</c>) and enrolled on a VM with the guest's
/// <c>construct backup recipients add</c>: at the end of each provision and adoption, and on a device's
/// request (<c>POST /vms/{name}/backup-recipients</c>). A device removes another recipient (an old per-PC
/// key) with <c>DELETE /vms/{name}/backup-recipients/{fingerprint}</c>, which the engine runs only while
/// the account's key is enrolled on that VM, so the VM always keeps a recipient a device holds.
/// </summary>
public sealed class AccountBackupKeys(EngineKeyStore keys, IProvisionTargets targets, ISshRunner ssh)
{
    /// <summary>The recipient label on the guest, as the converter enrolled it.</summary>
    public const string Label = "construct-account";

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The SubjectPublicKeyInfo PEM of an RSA public key of at least 3072 bits; null when the text is not one.</summary>
    public static string? Normalize(string? pem)
    {
        if (pem is null || pem.Length > 16 * 1024) return null;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return rsa.KeySize >= 3072 ? rsa.ExportSubjectPublicKeyInfoPem() : null;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return null;
        }
    }

    public void Register(string account, string normalizedPem) => keys.PutBackupKey(account, normalizedPem);

    /// <summary>The account's <c>B.pub</c> and its fingerprint, or null when no device registered it here.</summary>
    public (string Pem, string Fingerprint)? Of(string owner) =>
        keys.BackupKey(owner) is { } pem && BackupJobKey.PublicFingerprint(pem) is { } fingerprint ? (pem, fingerprint) : null;

    /// <summary>
    /// Enrols the account's key on <paramref name="vm"/> over the engine's SSH. The guest's exit code (0: enrolled),
    /// -1 when the VM has no SSH target or did not answer, or null when no device registered a key for its owner.
    /// </summary>
    public async Task<int?> AddAsync(Vm vm, CancellationToken ct)
    {
        if (Of(vm.Owner) is not { } key) return null;
        return await RunAsync(vm, BackupScript.RecipientsAdd(Label), key.Pem, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the recipient <paramref name="fingerprint"/> from <paramref name="vm"/> when the account's key is
    /// enrolled there and is not that one.
    /// </summary>
    public async Task<RemoveOutcome> RemoveAsync(Vm vm, string fingerprint, CancellationToken ct)
    {
        if (Of(vm.Owner) is not { } key) return RemoveOutcome.NoAccountKey;
        if (fingerprint == key.Fingerprint) return RemoveOutcome.AccountKey;
        var lines = new List<string>();
        var listed = await RunAsync(vm, BackupScript.RecipientsList, null, lines, ct).ConfigureAwait(false);
        if (listed != 0) return RemoveOutcome.Unreachable;
        if (BackupScript.RecipientsListed(lines) is not { } enrolled || !enrolled.Contains(key.Fingerprint)) return RemoveOutcome.AccountKeyMissing;
        if (!enrolled.Contains(fingerprint)) return RemoveOutcome.Removed;
        return await RunAsync(vm, BackupScript.RecipientsRemove(fingerprint), null, null, ct).ConfigureAwait(false) == 0
            ? RemoveOutcome.Removed
            : RemoveOutcome.Unreachable;
    }

    private async Task<int> RunAsync(Vm vm, string command, string? input, List<string>? output, CancellationToken ct)
    {
        if (await targets.ResolveAsync(vm, ct).ConfigureAwait(false) is not { } target) return -1;
        try
        {
            return await ssh.RunAsync(target, ProvisionScript.AsRoot(command, target.IsRoot, false), input,
                output is null ? null : new Lines(output), Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return -1;
        }
    }

    public enum RemoveOutcome { Removed, NoAccountKey, AccountKey, AccountKeyMissing, Unreachable }

    private sealed class Lines(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value.Trim());
        }
    }
}
