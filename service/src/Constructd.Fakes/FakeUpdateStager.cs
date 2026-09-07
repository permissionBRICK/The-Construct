using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class FakeUpdateStager : IUpdateStager
{
    public Dictionary<string, StagedUpdate> Staged { get; } = [];
    public bool VerificationSucceeds { get; set; } = true;
    public Task<StagedUpdate> StageAsync(string updateId, ReleaseDescriptor release, IProgress<string>? progress, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(Staged.TryGetValue(updateId, out var staged) ? staged : throw new InvalidOperationException("No staged fixture configured.")); }
    public Task<bool> VerifyStagedAsync(StagedUpdate staged, CancellationToken ct) => Task.FromResult(VerificationSucceeds && Staged.GetValueOrDefault(staged.UpdateId) == staged);
    public Task RemoveStagedAsync(string updateId, CancellationToken ct) { Staged.Remove(updateId); return Task.CompletedTask; }
}
