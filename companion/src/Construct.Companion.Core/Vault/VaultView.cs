using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
namespace Construct.Companion.Core.Vault;

// What only the native Key Vault window does, because it touches a value: the add/edit dialog, the
// clipboard and the phone pairing code. The page in the window never sees any of it.
public interface IVaultWindow
{
    // The add (existing null) or edit dialog. save stores what the user entered and returns null, or the
    // problem the dialog shows while it stays open.
    Task EditSecretAsync(VaultSecretView? existing, Func<VaultSecretInput, string?> save, CancellationToken cancellationToken);
    // False when the clipboard is unavailable. A sensitive text leaves the clipboard again after
    // VaultView.ClipboardLifetime unless something replaced it.
    Task<bool> CopyAsync(Secret text, bool sensitive, CancellationToken cancellationToken);
    Task ShowPairingAsync(VaultPairing pairing, CancellationToken cancellationToken);
}

// One request of the Key Vault page ({type:"vault.<action>", …}), validated by VaultView.Parse.
public sealed record VaultCommand(string Action, string Name = "", string Host = "", string Id = "", string Mode = "", string Vm = "");
// What the page shows after a command: an error, or a confirmation such as "Copied".
public sealed record VaultNotice(string Text, bool Error);

// The Key Vault window's decisions. The window shows media/vault.html and only forwards: the page gets
// State() (names, descriptions, usernames, counts and access metadata, never a value, the vault key, a
// device token or a pairing link) and its requests come back through Parse and ExecuteAsync, in process.
// They never pass the message dispatcher, IPC or HTTP, so nothing reachable over the Companion's local
// API can list or change the vault.
public sealed class VaultView(VaultService vault, VaultHosts hosts, IPrompts prompts, IVaultWindow window, IClock clock)
{
    public const string Title = "Construct Key Vault";
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30), ClipboardLifetime = TimeSpan.FromSeconds(30);
    private const string Prefix = "vault.";
    private static readonly VaultNotice SecretGone = new("That secret no longer exists.", true), LeaseGone = new("That access has already ended.", true),
        HostGone = new("That host is no longer enrolled.", true), DeviceGone = new("That device is no longer paired.", true),
        NoClipboard = new("The clipboard is unavailable.", true), Failed = new("The key vault could not complete this.", true);
    public static IReadOnlySet<string> Actions { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ready", "refresh", "add", "edit", "delete", "copySecret", "copyUsername", "revokeLease", "discardScrubs", "reset",
        "sync", "setMode", "pairPhone", "revokeDevice", "showKey", "importKey"
    };

    // ── the page's state ───────────────────────────────────────────────────────
    public JsonObject State()
    {
        var now = clock.UtcNow;
        var views = hosts.Views();
        var hostLeases = hosts.Leases().Where(l => l.Lease.ExpiresAt > now).ToArray();
        var secrets = new JsonArray();
        foreach (var s in vault.Secrets())
            secrets.Add(new JsonObject
            {
                ["name"] = s.Name, ["description"] = s.Description, ["username"] = s.Username,
                ["addedBy"] = s.Origin.StartsWith("agent:", StringComparison.Ordinal) ? s.Origin["agent:".Length..] : "",
                ["updatedAt"] = VaultSync.Ms(s.UpdatedAt),
                ["holders"] = s.ActiveLeases + hostLeases.Count(l => l.Lease.Name == s.Name)
            });
        var leases = new JsonArray();
        foreach (var l in vault.Leases()) leases.Add(Lease(l.Id, "", "", l.Instance, l.Name, l.UsesLeft, l.ExpiresAt, l.Reason, l.Origin));
        foreach (var h in hostLeases) leases.Add(Lease(h.Lease.Id, h.Slug, h.Host, h.Lease.Vm, h.Lease.Name, h.Lease.UsesLeft, h.Lease.ExpiresAt, h.Lease.Reason, h.Lease.Origin));
        var scrubs = new JsonArray();
        foreach (var group in vault.PendingCleanups().GroupBy(c => c.Instance, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            scrubs.Add(new JsonObject
            {
                ["vm"] = group.Key, ["names"] = Strings(group.Select(c => c.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)),
                ["dueAt"] = VaultSync.Ms(group.Min(c => c.DueAt))
            });
        var activity = new JsonArray();
        foreach (var a in vault.Activity().Reverse())
            activity.Add(new JsonObject { ["at"] = VaultSync.Ms(a.At), ["vm"] = a.Instance, ["host"] = a.Host, ["text"] = a.Text, ["warning"] = a.Warning });
        var hostList = new JsonArray();
        foreach (var h in views)
        {
            var (status, level) = h.State.NeedsKey ? ("Needs the vault key: Import vault key…", "warn")
                : h.State.LastError.Length > 0 ? (h.State.LastError, "warn")
                : h.State.LastSyncAt is null ? ("Not synced yet", "idle") : ("In sync", "ok");
            var devices = new JsonArray();
            foreach (var d in h.Devices)
                devices.Add(new JsonObject { ["id"] = d.Id, ["label"] = d.Label, ["createdAt"] = Ms(d.CreatedAt), ["lastUsedAt"] = Ms(d.LastUsedAt) });
            hostList.Add(new JsonObject
            {
                ["slug"] = h.Slug, ["host"] = h.Host, ["mode"] = h.State.Mode, ["lastSyncAt"] = Ms(h.State.LastSyncAt),
                ["status"] = status, ["level"] = level, ["needsKey"] = h.State.NeedsKey,
                ["online"] = h.Online, ["instances"] = Strings(h.Instances), ["devices"] = devices
            });
        }
        return new JsonObject
        {
            ["now"] = VaultSync.Ms(now), ["unavailable"] = vault.Unavailable, ["hasKey"] = vault.HasKey,
            ["secrets"] = secrets, ["leases"] = leases, ["scrubs"] = scrubs, ["activity"] = activity, ["hosts"] = hostList
        };
    }
    public JsonObject StateMessage() => new() { ["type"] = "vault.state", ["state"] = State() };
    // Sent after every command, so the page can release the control that asked.
    public static JsonObject DoneMessage(string action, VaultNotice? notice) => new()
    {
        ["type"] = "vault.done", ["action"] = action,
        ["notice"] = notice is null ? null : new JsonObject { ["text"] = notice.Text, ["error"] = notice.Error }
    };
    private static JsonObject Lease(string id, string slug, string host, string vm, string name, int? usesLeft, DateTimeOffset expiresAt, string reason, string origin) => new()
    {
        ["id"] = id, ["host"] = slug, ["hostName"] = host, ["vm"] = vm, ["name"] = name, ["usesLeft"] = usesLeft,
        ["expiresAt"] = VaultSync.Ms(expiresAt), ["reason"] = reason, ["origin"] = origin
    };
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
    private static long? Ms(DateTimeOffset? at) => at is { } value ? VaultSync.Ms(value) : null;

    // ── the page's requests ────────────────────────────────────────────────────
    // The page is untrusted input like any webview: an unknown type or a malformed field is refused here.
    public static VaultCommand Parse(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() is not { } kind || !kind.StartsWith(Prefix, StringComparison.Ordinal) || !Actions.Contains(kind[Prefix.Length..]))
            throw new ArgumentException("Unknown key vault request.");
        var action = kind[Prefix.Length..];
        string Field(string key, Func<string, bool> valid, bool optional = false)
        {
            if (!message.TryGetProperty(key, out var node) || node.ValueKind == JsonValueKind.Null)
                return optional ? "" : throw new ArgumentException($"The key vault request has no {key}.");
            if (node.ValueKind != JsonValueKind.String || node.GetString() is not { } value || !((optional && value.Length == 0) || valid(value)))
                throw new ArgumentException($"The key vault request has an invalid {key}.");
            return value;
        }
        return action switch
        {
            "edit" or "delete" or "copySecret" or "copyUsername" => new(action, Name: Field("name", VaultProtocol.IsValidName)),
            "revokeLease" => new(action, Id: Field("id", IsId), Host: Field("host", IsLabel, optional: true)),
            "discardScrubs" => new(action, Vm: Field("vm", IsLabel)),
            "setMode" => new(action, Host: Field("host", IsLabel), Mode: Field("mode", m => m is VaultSync.Available or VaultSync.Locked)),
            "pairPhone" => new(action, Host: Field("host", IsLabel)),
            "revokeDevice" => new(action, Host: Field("host", IsLabel), Id: Field("id", IsId)),
            _ => new(action)
        };
    }
    private static bool IsId(string id) => id.Length is > 0 and <= 128 && !id.Any(char.IsControl) && !id.Contains('/', StringComparison.Ordinal);
    private static bool IsLabel(string text) => text.Length is > 0 and <= 200 && !text.Any(char.IsControl);

    // Runs one request. Everything is checked against the current vault first (a stale page may name a
    // secret, lease or device that is gone); destructive requests ask in a native dialog. Null = nothing to show.
    public async Task<VaultNotice?> ExecuteAsync(VaultCommand command, CancellationToken cancellationToken)
    {
        if (!Actions.Contains(command.Action)) throw new ArgumentException("Unknown key vault request.");
        try { return await RunAsync(command, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // Host, pairing and vault errors are written for the user and never carry a secret; anything else stays generic.
        catch (Exception e) when (e is RemoteApiException or InvalidOperationException or VaultUnavailableException) { return new(e.Message, true); }
        catch (Exception) { return Failed; }
    }
    private async Task<VaultNotice?> RunAsync(VaultCommand c, CancellationToken ct)
    {
        switch (c.Action)
        {
            case "ready": return null;
            case "refresh": await hosts.RefreshAsync(ct).ConfigureAwait(false); return null;
            case "add": await window.EditSecretAsync(null, input => Save(input, null), ct).ConfigureAwait(false); return null;
            case "edit":
                if (Find(c.Name) is not { } existing) return SecretGone;
                await window.EditSecretAsync(existing, input => Save(input, c.Name), ct).ConfigureAwait(false); return null;
            case "delete":
                if (Find(c.Name) is null) return SecretGone;
                if (await ConfirmAsync($"Delete “{c.Name}” from the key vault? VMs that hold it lose access and are scrubbed of it.", "Delete", ct).ConfigureAwait(false))
                    vault.Delete(c.Name);
                return null;
            case "copySecret":
                if (vault.Reveal(c.Name) is not { } value) return SecretGone;
                return await window.CopyAsync(value, true, ct).ConfigureAwait(false)
                    ? new($"Copied the secret of {c.Name}. It leaves the clipboard again in {ClipboardLifetime.TotalSeconds:0} seconds.", false) : NoClipboard;
            case "copyUsername":
                if (Find(c.Name) is null) return SecretGone;
                if (vault.Username(c.Name) is not { Length: > 0 } username) return new($"{c.Name} has no username.", true);
                return await window.CopyAsync(new Secret(username), false, ct).ConfigureAwait(false) ? new($"Copied the username of {c.Name}.", false) : NoClipboard;
            case "revokeLease":
                if (c.Host.Length == 0)
                {
                    if (!vault.Leases().Any(l => l.Id == c.Id)) return LeaseGone;
                    vault.Revoke(c.Id); return null;
                }
                if (!hosts.Leases().Any(l => l.Slug == c.Host && l.Lease.Id == c.Id)) return LeaseGone;
                return Problem(await hosts.RevokeLeaseAsync(c.Host, c.Id, ct).ConfigureAwait(false));
            case "discardScrubs":
                if (!vault.PendingCleanups().Any(p => p.Instance == c.Vm)) return null;
                if (await ConfirmAsync($"Forget the pending scrubs for “{c.Vm}”? Copies of those secrets may stay on that VM.", "Forget scrubs", ct).ConfigureAwait(false))
                    vault.DiscardCleanups(c.Vm);
                return null;
            case "reset":
                if (vault.Unavailable is null) return null;
                if (!await ConfirmAsync("Start a new, empty key vault? The unreadable file is renamed and kept, so it can still be restored on the PC and account that created it.", "Start a new vault", ct).ConfigureAwait(false))
                    return null;
                return new("A new, empty key vault is in use. The old vault file was kept as " + vault.ResetUnreadable(), false);
            case "sync": await hosts.SyncAsync(ct).ConfigureAwait(false); return null; // each host lists its own outcome
            case "setMode":
                if (Host(c.Host) is not { } host) return HostGone;
                if (host.State.Mode == c.Mode) return null;
                var locked = c.Mode == VaultSync.Locked;
                if (!await ConfirmAsync(locked
                        ? $"Lock the vault on {host.Host} to your PC? It then opens only for VMs this PC starts or connects to, and stays open until that VM stops. VMs started without this PC cannot read secrets."
                        : $"Make the vault on {host.Host} always available? The host keeps the vault key, so your VMs can use secrets (with your approval) while this PC is off.",
                        locked ? "Lock to my PC" : "Make always available", ct).ConfigureAwait(false)) return null;
                return Problem(await hosts.SetModeAsync(c.Host, c.Mode, ct).ConfigureAwait(false));
            case "pairPhone":
                if (Host(c.Host) is null) return HostGone;
                if (await hosts.PairPhoneAsync(c.Host, ct).ConfigureAwait(false) is { } pairing) await window.ShowPairingAsync(pairing, ct).ConfigureAwait(false);
                return null;
            case "revokeDevice":
                if (Host(c.Host) is not { } paired || paired.Devices.FirstOrDefault(d => d.Id == c.Id) is not { } device) return DeviceGone;
                if (!await ConfirmAsync($"Revoke “{device.Label}”? It can no longer approve requests; pair it again to restore it.", "Revoke", ct).ConfigureAwait(false)) return null;
                return Problem(await hosts.RevokeDeviceAsync(c.Host, c.Id, ct).ConfigureAwait(false));
            case "showKey": return Problem(await hosts.ShowKeyAsync(ct).ConfigureAwait(false));
            case "importKey": return Problem(await hosts.ImportKeyAsync(ct).ConfigureAwait(false));
            default: throw new ArgumentException("Unknown key vault request.");
        }
    }
    private VaultSecretView? Find(string name) => vault.Secrets().FirstOrDefault(s => s.Name == name);
    private VaultHostView? Host(string slug) => hosts.Views().FirstOrDefault(h => h.Slug == slug);
    private static VaultNotice? Problem(string? text) => text is null ? null : new(text, true);
    private Task<bool> ConfirmAsync(string message, string action, CancellationToken ct) => prompts.ConfirmAsync(new ConfirmationPrompt(Title, message, action), ct);
    // The editor's save: rule violations stay in the dialog, which keeps the entered value for a correction.
    private string? Save(VaultSecretInput input, string? original)
    {
        try { vault.Save(input, original); return null; }
        catch (Exception e) when (e is ArgumentException or VaultUnavailableException) { return e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "The key vault could not be saved."; }
    }
}
