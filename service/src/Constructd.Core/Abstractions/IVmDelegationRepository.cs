using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public enum VmAddDecision { Added, NameTaken, PrimaryQuotaExceeded, ChildrenQuotaExceeded, ParentClosed, ParentMissing }

public interface IVmDelegationRepository
{
    Task<IReadOnlyList<Vm>> ListChildrenAsync(string parent, CancellationToken ct);
    Task<IReadOnlyList<Vm>> ListSharedAsync(SharingScope scope, CancellationToken ct);
    Task<int> CountByOwnerAsync(string owner, VmKind kind, CancellationToken ct);
    /// <summary>Atomic: name free, quota by kind, parent present/not fenced, incarnation assigned.</summary>
    Task<VmAddDecision> AddAsync(Vm vm, EffectiveAllowance allowance, CancellationToken ct);
    /// <summary>Sets Deleting (+ optionally ChildCreationClosed) and CurrentJobId in one write; false when already fenced by a live job.</summary>
    Task<bool> TryFenceAsync(string name, string jobId, bool closeChildCreation, CancellationToken ct);
    Task<bool> UpdateLeaseAsync(string name, Lease lease, long expectedVersion, CancellationToken ct);
    Task<IReadOnlyList<Vm>> ListLeasesDueAsync(DateTimeOffset now, TimeSpan retryAfter, CancellationToken ct);
    Task<VmOverride?> GetOverrideAsync(string vmName, CancellationToken ct);
    Task SetOverrideAsync(VmOverride value, CancellationToken ct);
    Task<bool> RemoveOverrideAsync(string vmName, CancellationToken ct);
    Task<CascadePreview> SaveCascadePreviewAsync(CascadePreview preview, CancellationToken ct);
    Task<CascadePreview?> GetCascadePreviewAsync(string parent, CancellationToken ct);
    /// <summary>One transaction: token/children/sharing/incarnations equal the stored preview → fence parent + children; else the current list.</summary>
    Task<CascadeAcceptance> TryAcceptCascadeAsync(string parent, string token, string jobId, CancellationToken ct);
    Task<bool> UpdateGuestReportAsync(string name, GuestReport report, CancellationToken ct);
    Task<bool> UpdateObservationAsync(string name, HostObservation observation, CancellationToken ct);
}

public sealed record CascadeAcceptance(bool Accepted, string? Reason, IReadOnlyList<CascadeChild> CurrentChildren, string? NewToken);
