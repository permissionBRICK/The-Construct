using Constructd.Core.Domain;
namespace Constructd.Core.Abstractions;

/// <summary>Accept the update state transition, queued job and replay key in one durable transaction.</summary>
public interface IHostUpdateAdmission
{
    Task<bool> TryAcceptAsync(HostUpdateRecord row, Job queued, OperationKeyRecord? operation, bool fresh, CancellationToken ct);
}
