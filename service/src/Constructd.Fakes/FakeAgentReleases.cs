using System.Collections.Concurrent;
using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>Latest agent versions a test sets; unknown tools have none.</summary>
public sealed class FakeAgentReleases : IAgentReleases
{
    public ConcurrentDictionary<string, string> Latest { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> LatestAsync(string tool, string? channel, CancellationToken ct) =>
        Task.FromResult(Latest.TryGetValue(channel == "nightly" ? tool + "@nightly" : tool, out var v) ? v : null);
}
