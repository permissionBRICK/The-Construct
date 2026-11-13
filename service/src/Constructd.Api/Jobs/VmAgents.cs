using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Jobs;

/// <summary>One coding agent the guest found installed, with the version its <c>--version</c> printed.</summary>
public sealed record VmAgentVersion(string Tool, string Version);

/// <summary>
/// What a VM last reported about its coding agents, carried by its token-usage report. Bound to the VM's
/// creation time, so a VM created again under the name starts without its predecessor's report.
/// </summary>
public sealed record VmAgentsReport(DateTimeOffset VmCreated, DateTimeOffset ReportedAt, IReadOnlyList<VmAgentVersion> Agents);

/// <summary>The coding agents of a VM (Claude Code, Codex, OpenCode, T3 Code), stored per VM in the host config store.</summary>
public sealed partial class VmAgents(IHostConfigStore config)
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["claude"] = "Claude Code", ["codex"] = "Codex", ["opencode"] = "OpenCode", ["t3"] = "T3 Code",
    };

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.+-]{1,48})?$")]
    public static partial Regex VersionPattern();

    private static string Key(string vm) => "vm-agents:" + vm.ToLowerInvariant();

    public Task SaveAsync(Vm vm, IReadOnlyList<VmAgentVersion> agents, DateTimeOffset at, CancellationToken ct) =>
        config.SetAsync(Key(vm.Name), new VmAgentsReport(vm.Created, at, agents), "vm:" + vm.Name, ct);

    public async Task<VmAgentsReport?> GetAsync(Vm vm, CancellationToken ct) =>
        await config.GetAsync<VmAgentsReport>(Key(vm.Name), ct) is { } report && report.VmCreated == vm.Created ? report : null;

    /// <summary>True when <paramref name="latest"/> is a newer major.minor.patch than <paramref name="installed"/> (the old panel's rule).</summary>
    public static bool IsNewer(string? latest, string installed)
    {
        static int[]? Parts(string? v) => v is not null && Regex.Match(v, @"(\d+)\.(\d+)\.(\d+)") is { Success: true } m
            && int.TryParse(m.Groups[1].Value, out var a) && int.TryParse(m.Groups[2].Value, out var b) && int.TryParse(m.Groups[3].Value, out var c)
            ? [a, b, c] : null;
        if (Parts(latest) is not { } l || Parts(installed) is not { } i) return false;
        for (var n = 0; n < 3; n++) if (l[n] != i[n]) return l[n] > i[n];
        return false;
    }
}

/// <summary>
/// The newest agent releases from where the old panel read them (<c>extension/src/updates.js</c>), each cached for
/// six hours; a failed lookup is remembered for a few minutes and answers null.
/// </summary>
public sealed class AgentReleases(HttpClient http, IClock clock) : IAgentReleases
{
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan Failed = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (string? Version, DateTimeOffset Until)> _cache = new();

    public async Task<string?> LatestAsync(string tool, string? channel, CancellationToken ct)
    {
        var (url, field) = tool switch
        {
            "claude" => ("https://registry.npmjs.org/@anthropic-ai/claude-code/latest", "version"),
            "codex" => ("https://api.github.com/repos/openai/codex/releases/latest", "tag_name"),
            "opencode" => ("https://api.github.com/repos/sst/opencode/releases/latest", "tag_name"),
            "t3" => (channel == "nightly" ? "https://registry.npmjs.org/t3/nightly" : "https://registry.npmjs.org/t3/latest", "version"),
            _ => ((string?)null, ""),
        };
        if (url is null) return null;
        if (_cache.TryGetValue(url, out var hit) && hit.Until > clock.UtcNow) return hit.Version;
        string? version = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("constructd", "1"));
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                    version = System.Text.RegularExpressions.Regex.Match(value.GetString()!, @"\d+\.\d+\.\d+[0-9A-Za-z.+-]*").Value is { Length: > 0 } v ? v : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        _cache[url] = (version, clock.UtcNow + (version is null ? Failed : Fresh));
        return version;
    }
}
