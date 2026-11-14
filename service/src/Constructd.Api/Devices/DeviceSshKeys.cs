using System.Buffers.Binary;
using Construct.Keys;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Store.Client;
using Constructd.Sync.Participation;

namespace Constructd.Api.Devices;

/// <summary>
/// The SSH keys of a user's devices on that user's VMs (docs/plans/construct-platform-design.md 5.1,
/// 7.2): a device opens forwards, reads notifications and opens VS Code or a terminal over SSH, so its
/// key must be authorized on every primary VM its user owns. A device registers one key here with its
/// device token (<c>PUT /device/ssh-key</c>); the engine writes the keys of the account's active
/// devices into the VM's <c>/root/.ssh/authorized_keys</c>:
/// at the end of each provision, when a device registers or is revoked (or the device records say
/// so, read by the profile sync loop), and for a VM it could not reach then, at that VM's next heartbeat.
///
/// Root's file because the device connects as root, as the old client did: the agent's alias and its
/// tunnels use <c>User root</c> (<c>SshClientConfig</c>, the Companion's <c>SshArgs</c> default), and
/// the provisioner keeps root's file for keys like these (<c>setup-root-ssh-key.sh</c> only appends).
///
/// Only lines that end in <c>construct-device:&lt;device id&gt;</c> are the engine's; each write
/// replaces exactly those and keeps every other line as it is. What was written to which VM is
/// remembered in memory only: after a restart every running VM is written once more.
/// </summary>
public sealed partial class DeviceSshKeys(EngineKeyStore keys, EngineDevices devices, IVmRepository vms, IProvisionTargets targets,
    ISshRunner ssh, ILogger<DeviceSshKeys> log, IHostApplicationLifetime lifetime)
{
    // Background writes end with the engine.
    private CancellationToken Stopping => lifetime.ApplicationStopping;

    /// <summary>The comment that marks a line as the engine's.</summary>
    public const string Marker = "construct-device:";

    /// <summary>The script prints this when the file was written.</summary>
    public const string Done = "construct-device-keys-written";

    private static readonly TimeSpan SshTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> KeyTypes = new(StringComparer.Ordinal)
    {
        "ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384", "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com", "sk-ecdsa-sha2-nistp256@openssh.com",
    };

    // VM (name and creation time) -> hash of the key block written there, and of the block that last failed.
    private readonly ConcurrentDictionary<string, string> _applied = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>
    /// <c>type base64</c> of an OpenSSH public key line, the comment dropped; null when the text is not
    /// one public key of a known type on one line.
    /// </summary>
    public static string? Normalize(string? line)
    {
        if (line is null || line.Length > 16 * 1024 || line.Contains('\n') || line.Contains('\r')) return null;
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || !KeyTypes.Contains(fields[0]) || !Base64().IsMatch(fields[1])) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(fields[1]); }
        catch (FormatException) { return null; }

        // The blob starts with its own type name: a key pasted under another type is refused.
        if (blob.Length < 4) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length != fields[0].Length || blob.Length < 4 + length || Encoding.ASCII.GetString(blob, 4, (int)length) != fields[0]) return null;
        return fields[0] + " " + fields[1];
    }

    /// <summary>
    /// Whether the engine manages device keys for the account: only an account that granted this
    /// engine <c>D</c> has devices here (enrolment reads the device records with it).
    /// </summary>
    public bool Manages(string account) => Account(account) is not null;

    /// <summary>The account as it granted <c>D</c> here (user names compare without case, as ownership does).</summary>
    private string? Account(string owner) => keys.GrantingAccounts(EngineGrants.Records).FirstOrDefault(a => Ownership.SameName(a, owner));

    /// <summary>Registers (or replaces) a device's key and writes the account's VMs.</summary>
    public void Register(string account, string deviceId, string normalizedKey)
    {
        keys.PutDeviceSshKey(account, deviceId, normalizedKey);
        Changed(account);
    }

    /// <summary>The account's devices changed (a registration, a revocation): its VMs are written in the background.</summary>
    public void Changed(string account) => _ = Task.Run(() => SyncAccountAsync(account, Stopping));

    /// <summary>The key lines for the account's VMs: the keys of its active devices, marked.</summary>
    public async Task<IReadOnlyList<string>> LinesAsync(string owner, CancellationToken ct)
    {
        if (Account(owner) is not { } account) return [];
        var stored = keys.DeviceSshKeys(account);
        if (stored.Count == 0) return [];
        IReadOnlySet<string>? active = null;
        try
        {
            active = await devices.ActiveAsync(account, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StoreUnavailableException or HttpRequestException or CryptographicException)
        {
            // A revocation deletes the stored key at once, so the stored keys are the active devices'
            // as far as this engine has seen.
        }

        return stored.Where(k => active is null || active.Contains(k.DeviceId)).Select(k => $"{k.PublicKey} {Marker}{k.DeviceId}").ToList();
    }

    /// <summary>
    /// The guest script: replaces the marked lines of root's <c>authorized_keys</c> with
    /// <paramref name="lines"/> (none: removes them) and keeps every other line. The path comes
    /// from <c>CONSTRUCT_AUTHORIZED_KEYS</c> when set (tests).
    /// </summary>
    public static string Script(IReadOnlyList<string> lines)
    {
        var builder = new StringBuilder();
        builder.Append("""
            set -eu
            f="${CONSTRUCT_AUTHORIZED_KEYS:-/root/.ssh/authorized_keys}"
            d="$(dirname "$f")"
            mkdir -p "$d"
            chmod 700 "$d"
            touch "$f"
            t="$(mktemp "$f.construct.XXXXXX")"
            trap 'rm -f "$t"' EXIT
            { grep -v -E ' construct-device:[0-9a-z]+$' "$f" || [ $? -eq 1 ]; } > "$t"
            cat >> "$t" <<'CONSTRUCT_DEVICE_KEYS'

            """);
        foreach (var line in lines) builder.Append(line).Append('\n');
        builder.Append("""
            CONSTRUCT_DEVICE_KEYS
            chmod 600 "$t"
            mv -f "$t" "$f"
            trap - EXIT
            echo construct-device-keys-written

            """);
        return builder.ToString();
    }

    public static string Hash(IReadOnlyList<string> lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    private static string Slot(Vm vm) => vm.Name.ToLowerInvariant() + "|" + vm.Created.ToUnixTimeMilliseconds();

    private void Written(Vm vm, string hash)
    {
        _applied[Slot(vm)] = hash;
        _failed.TryRemove(Slot(vm), out _);
    }

    /// <summary>The VM's guest is new or rebuilt (a provision starts): what was written to the old one no longer counts.</summary>
    public void Forget(Vm vm)
    {
        _applied.TryRemove(Slot(vm), out _);
        _failed.TryRemove(Slot(vm), out _);
    }

    /// <summary>Writes every running primary VM of the account whose keys differ, except those that failed with these keys (their heartbeat retries).</summary>
    public async Task SyncAccountAsync(string account, CancellationToken ct)
    {
        if (!Manages(account)) return;
        try
        {
            var lines = await LinesAsync(account, ct).ConfigureAwait(false);
            var hash = Hash(lines);
            foreach (var vm in await OwnedRunningAsync(account, ct).ConfigureAwait(false))
            {
                if (_applied.TryGetValue(Slot(vm), out var done) && done == hash) continue;
                if (_failed.TryGetValue(Slot(vm), out var failed) && failed == hash) continue;
                await WriteAsync(vm, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Device keys of account {Account} were not written: {Reason}", account, ex.Message);
        }
    }

    /// <summary>A VM's heartbeat: when its keys are not the current ones, they are written in the background.</summary>
    public async Task HeartbeatAsync(Vm vm, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary || !Manages(vm.Owner)) return;
        var hash = Hash(await LinesAsync(vm.Owner, ct).ConfigureAwait(false));
        if (_applied.TryGetValue(Slot(vm), out var done) && done == hash) return;
        if (!_running.TryAdd(Slot(vm), true)) return;
        _ = Task.Run(async () =>
        {
            try { await WriteAsync(vm, Stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning("Device keys were not written into {Vm}: {Reason}", vm.Name, ex.Message); }
            finally { _running.TryRemove(Slot(vm), out _); }
        }, Stopping);
    }

    /// <summary>Writes the account's current keys into one VM over the engine's SSH; true when it was written.</summary>
    public Task<bool> WriteAsync(Vm vm, CancellationToken ct) => WriteWithAsync(vm, async (script, token) =>
    {
        if (await targets.ResolveAsync(vm, token).ConfigureAwait(false) is not { } target) return false;
        var output = new List<string>();
        int exit;
        try
        {
            exit = await ssh.RunAsync(target, target.IsRoot ? "bash -s" : "sudo -n bash -s", script, new Lines(output), SshTimeout, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            exit = -1;
        }

        lock (output) return exit == 0 && output.Contains(Done);
    }, ct);

    /// <summary>
    /// Writes the account's current keys into one VM with <paramref name="run"/> (the script; true when it printed
    /// <see cref="Done"/>). One write at a time, the lines read inside, so the last write carries the newest keys; and
    /// only while the VM is still the one asked about (same name, owner and creation), never a VM that replaced it.
    /// </summary>
    public async Task<bool> WriteWithAsync(Vm vm, Func<string, CancellationToken, Task<bool>> run, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await vms.GetAsync(vm.Name, ct).ConfigureAwait(false);
            if (current is null || current.Deleting || current.Created != vm.Created || !Ownership.SameName(current.Owner, vm.Owner)) return false;
            var lines = await LinesAsync(current.Owner, ct).ConfigureAwait(false);
            var hash = Hash(lines);
            if (await run(Script(lines), ct).ConfigureAwait(false))
            {
                Written(current, hash);
                return true;
            }

            _applied.TryRemove(Slot(current), out _);
            _failed[Slot(current)] = hash;
            log.LogInformation("Device keys could not be written into {Vm}; trying again at its next heartbeat.", vm.Name);
            return false;
        }
        finally { _write.Release(); }
    }

    private async Task<IReadOnlyList<Vm>> OwnedRunningAsync(string account, CancellationToken ct) =>
        (await vms.ListAsync(null, ct).ConfigureAwait(false))
            .Where(v => Ownership.SameName(v.Owner, account) && v.Kind == VmKind.Primary && v.State == VmState.Running && !v.Deleting).ToList();

    [GeneratedRegex("^[A-Za-z0-9+/]+={0,2}$")]
    private static partial Regex Base64();

    private sealed class Lines(List<string> lines) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (lines) lines.Add(value.Trim());
        }
    }
}
