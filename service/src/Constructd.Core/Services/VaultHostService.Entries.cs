using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>
/// A pending secret-entry request as a device shows it (plan 10.3): the VM as this engine names it, then the
/// agent's description and reason as plain text it wrote.
/// </summary>
public sealed record VaultEntryRequestView(string Id, string Vm, string Name, string Description, string Reason, string Source,
    long CreatedAt, long ExpiresAt);

/// <summary>
/// Secret entry on request (plan 10.3, S21): <c>construct secret ask</c> files a request bound to the user, the VM and
/// a request id; a device enters the value. The entry is created only while the name is free, needs approval for
/// every use, is never auto-approved by this path and grants no lease. Declining, or a day passing, removes the
/// request and nothing else. Requests live in memory: a service restart forgets them, and the agent asks again.
/// </summary>
public sealed partial class VaultHostService
{
    public static readonly TimeSpan EntryRequestLifetime = TimeSpan.FromDays(1);
    public const int MaxEntryRequestsPerVm = 10;

    private sealed record EntryRequest(string Id, string Owner, string Vm, string Name, string Description, string Reason, string Source,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    private readonly Dictionary<string, EntryRequest> entryRequests = new(StringComparer.Ordinal);

    // The guest's ask: only for a record-backed vault, only for a free name; asking again for the same name is the same request.
    private JsonObject AskForEntry(Vm vm, VaultRequest request, VaultOwnerState state, IReadOnlyList<VaultEntry> entries)
    {
        var name = request.Names[0];
        if (!state.Records)
            return VaultProtocol.Response(request.Id, "error", "Asking the user to enter a secret needs a vault kept by your devices (The Construct's device agent).");
        if (Live(entries, name) is not null)
            return VaultProtocol.Response(request.Id, "exists", $"A secret named {name} already exists: request it (construct secret request {name}).");
        var now = clock.UtcNow;
        EntryRequest asked;
        lock (pendingGate)
        {
            ExpireEntryRequestsLocked(now);
            var mine = entryRequests.Values.Where(r => Ownership.SameName(r.Vm, vm.Name)).ToArray();
            if (mine.FirstOrDefault(r => r.Name == name) is { } existing) asked = existing;
            else if (mine.Length >= MaxEntryRequestsPerVm)
                return VaultProtocol.Response(request.Id, "error", "Too many entry requests from this VM are waiting for the user.");
            else
            {
                asked = new EntryRequest(NewId(), vm.Owner, vm.Name, name, request.Description, request.Reason, request.Source, now, now + EntryRequestLifetime);
                entryRequests[asked.Id] = asked;
                Notify(EntryRequestAdded, asked.Id, asked.Vm, asked.Owner);
            }
        }
        var response = VaultProtocol.Response(request.Id, "ok", "Requested.");
        response["names"] = new JsonArray(name);
        response["requestId"] = asked.Id;
        response["expiresAt"] = asked.ExpiresAt.ToUnixTimeMilliseconds();
        return response;
    }

    private IReadOnlyList<EntryRequest> EntryRequestsOf(Vm vm)
    {
        lock (pendingGate)
        {
            ExpireEntryRequestsLocked(clock.UtcNow);
            return entryRequests.Values.Where(r => Ownership.SameName(r.Vm, vm.Name)).ToArray();
        }
    }

    /// <summary><c>GET /vault/entry-requests</c>: the user's pending entry requests, oldest first.</summary>
    public IReadOnlyList<VaultEntryRequestView> EntryRequests(string owner)
    {
        lock (pendingGate)
        {
            ExpireEntryRequestsLocked(clock.UtcNow);
            return entryRequests.Values.Where(r => Ownership.SameName(r.Owner, owner)).OrderBy(r => r.CreatedAt)
                .Select(r => new VaultEntryRequestView(r.Id, r.Vm, r.Name, r.Description, r.Reason, r.Source,
                    r.CreatedAt.ToUnixTimeMilliseconds(), r.ExpiresAt.ToUnixTimeMilliseconds()))
                .ToArray();
        }
    }

    /// <summary><c>POST /vault/entry-requests/{id}/decline</c>: removes the request and nothing else.</summary>
    public async Task DeclineEntryAsync(string owner, string id, CancellationToken ct)
    {
        var request = TakeEntryRequest(owner, id, remove: true);
        await RecordAsync(owner, request.Vm, $"Declined to enter {request.Name} for the VM.", false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>POST /vault/entry-requests/{id}</c>: a device saves the value it sealed with <c>K</c> (9.1 row 4). The entry is
    /// created only while no live entry has the name; otherwise 409 <c>exists</c> and nothing is written. It needs approval
    /// for every use and grants no lease. A payload sealed as auto-approved is refused where this engine can check it.
    /// </summary>
    public async Task SaveEntryAsync(string owner, string id, string device, string? payload, long updatedAt, bool hasUsername, CancellationToken ct)
    {
        if (payload is null || payload.Length > MaxPayloadChars || !IsSealed(payload, 2)) throw Validation("payload", "The value sealed with the vault key.");
        if (updatedAt <= 0) throw Validation("updatedAt", "Expected Unix milliseconds.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = TakeEntryRequest(owner, id, remove: false);
            var state = await OwnerAsync(owner, ct).ConfigureAwait(false);
            if (state is not { Records: true }) throw new VaultProblemException(409, "vault-not-records", "This vault is not kept by your devices.");
            if (KeyFor(state, request.Vm) is { } key)
            {
                try
                {
                    if (SealedAutoApprove(key, request.Name, updatedAt, payload)) throw new VaultProblemException(400, "auto-approve", "A secret entered on request needs approval for every use.");
                }
                catch (CryptographicException) { throw new VaultProblemException(400, "wrong-key", "The value is not sealed with this vault's key for this name."); }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            var provenance = new VaultProvenance(VaultProvenance.EnteredOnRequest, request.Vm, device, request.Id);
            for (var attempt = 1; ; attempt++)
            {
                var entries = await EntriesAsync(owner, ct).ConfigureAwait(false);
                if (Live(entries, request.Name) is not null)
                    throw new VaultProblemException(409, "exists", $"A secret named {request.Name} exists already; nothing was overwritten.");
                var entry = new VaultEntry(request.Name, request.Description, hasUsername, payload, updatedAt, device, false, false);
                if (await SaveAsync(state, entry, BaseOf(entries, request.Name), provenance, [VmOrigin(request.Vm), device], ct).ConfigureAwait(false)) break;
                if (attempt >= MaxWriteAttempts) throw KeptChanging(request.Name);
            }
            lock (pendingGate)
                if (entryRequests.Remove(request.Id)) Notify(EntryRequestEnded, request.Id, request.Vm, request.Owner);
            await RecordAsync(owner, request.Vm, $"Entered {request.Name} on the VM's request; it needs approval for every use.", false, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // True when the payload opens for this name and stamp and carries auto-approve; throws when it does not open.
    private static bool SealedAutoApprove(byte[] key, string name, long updatedAt, string payload) =>
        Construct.Keys.VaultSeal.OpenEntry(key, name, updatedAt, payload).AutoApprove;

    private EntryRequest TakeEntryRequest(string owner, string id, bool remove)
    {
        lock (pendingGate)
        {
            ExpireEntryRequestsLocked(clock.UtcNow);
            if (!entryRequests.TryGetValue(id, out var request) || !Ownership.SameName(request.Owner, owner)) throw NotFound("No such entry request.");
            if (remove && entryRequests.Remove(id)) Notify(EntryRequestEnded, request.Id, request.Vm, request.Owner);
            return request;
        }
    }

    private void ExpireEntryRequests(DateTimeOffset now)
    {
        lock (pendingGate) ExpireEntryRequestsLocked(now);
    }

    private void ExpireEntryRequestsLocked(DateTimeOffset now)
    {
        foreach (var request in entryRequests.Values.Where(r => r.ExpiresAt <= now).ToArray())
            if (entryRequests.Remove(request.Id)) Notify(EntryRequestEnded, request.Id, request.Vm, request.Owner);
    }

    // A VM that goes away takes its entry requests with it.
    private void CancelEntryRequests(string vmName)
    {
        lock (pendingGate)
            foreach (var request in entryRequests.Values.Where(r => Ownership.SameName(r.Vm, vmName)).ToArray())
                if (entryRequests.Remove(request.Id)) Notify(EntryRequestEnded, request.Id, request.Vm, request.Owner);
    }
}
