using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public enum OperationKeyState { InFlight, Completed }
/// <param name="IntentJson">For external (hypervisor) mutations: the original intent (lifetime, expected lease version, the activation base) so a replay after a crash reconciles that intent instead of re-deciding.</param>
/// <param name="PowerGeneration">The VM's power generation the intent was accepted at (§5.3b); a replay whose VM moved past it is a conflict.</param>
public sealed record OperationKeyRecord(string Owner, string Kind, string Key, string Fingerprint, string Target, string? JobId, OperationKeyState State, string? IntentJson, long? PowerGeneration, string? ResponseJson, DateTimeOffset Created);
public enum OperationKeyOutcome { Inserted, Replay, Conflict }
public interface IOperationKeyStore
{
    Task<OperationKeyRecord?> GetAsync(string owner, string kind, string key, CancellationToken ct);
    /// <summary>INSERT OR FAIL of an InFlight record. In SQLite it runs inside the caller's transaction when one is supplied through the admission seam; standalone otherwise.</summary>
    Task<(OperationKeyOutcome Outcome, OperationKeyRecord? Existing)> TryInsertAsync(OperationKeyRecord record, CancellationToken ct);
    /// <summary>InFlight → Completed with the response, atomically with the database-only mutation it answers (§7.3).</summary>
    Task<bool> CompleteAsync(string owner, string kind, string key, string responseJson, CancellationToken ct);
    Task<bool> RemoveAsync(string owner, string kind, string key, CancellationToken ct);
    Task<IReadOnlyList<OperationKeyRecord>> ListInFlightAsync(string vmName, CancellationToken ct);
    Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken ct);
}
