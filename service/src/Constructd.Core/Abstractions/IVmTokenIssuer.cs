using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public interface IVmTokenIssuer
{
    /// <summary>Replaces the VM's token hash and kind in one write; returns the plaintext once.</summary>
    Task<string> IssueVmTokenAsync(string vmName, VmTokenKind kind, CancellationToken ct);
    Task<bool> RevokeVmTokenAsync(string vmName, CancellationToken ct);
}
