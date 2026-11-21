using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>
/// One coding agent the guest found installed, with the version its <c>--version</c> printed and whether it is
/// signed in (null: not reported).
/// </summary>
public sealed record VmAgentVersion(string Tool, string Version, bool? SignedIn = null);

/// <summary>
/// A lost sign-in (S36): the VM reported <paramref name="Tool"/> signed out after it had been signed in, at
/// <paramref name="At"/>. It stays until the agent is signed in again or the owner dismisses it; a dismissed one comes
/// back only after the agent was signed in and lost again.
/// </summary>
public sealed record VmAgentLost(string Tool, DateTimeOffset At);

/// <summary>
/// What a VM last reported about its coding agents, carried by its token-usage report. Bound to the VM's
/// creation time, so a VM created again under the name starts without its predecessor's report.
/// <paramref name="Lost"/>: the lost sign-ins waiting for the owner (null in a report stored before they existed).
/// </summary>
public sealed record VmAgentsReport(DateTimeOffset VmCreated, DateTimeOffset ReportedAt, IReadOnlyList<VmAgentVersion> Agents,
    IReadOnlyList<VmAgentLost>? Lost = null);

/// <summary>
/// The coding agents of a VM (Claude Code, Codex, OpenCode, Antigravity, T3 Code), stored per VM in the host config
/// store, with its lost sign-ins (S36). A lost sign-in that appears, clears or is dismissed is announced to the VM's
/// owner only (<c>agent-notice.changed</c>, its id the agent's tool id).
/// </summary>
public sealed partial class VmAgents(IHostConfigStore config, ChangeFeed changes)
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["claude"] = "Claude Code", ["codex"] = "Codex", ["opencode"] = "OpenCode", ["antigravity"] = "Antigravity", ["t3"] = "T3 Code",
    };

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.+-]{1,48})?$")]
    public static partial Regex VersionPattern();

    private static string Key(string vm) => "vm-agents:" + vm.ToLowerInvariant();

    /// <summary>
    /// One agent of a report, when it is a known tool with a version and not in <paramref name="into"/> yet. T3 Code has
    /// no sign-in of its own.
    /// </summary>
    public static bool Accept(string? tool, string? version, bool? signedIn, List<VmAgentVersion> into)
    {
        if (tool is null || !Names.ContainsKey(tool) || version is null || !VersionPattern().IsMatch(version) || into.Any(a => a.Tool == tool)) return false;
        into.Add(new(tool, version, tool == "t3" ? null : signedIn));
        return true;
    }

    /// <summary>The guest's agent report as <c>construct-usage-report.sh --agents</c> prints it; null when it is not one.</summary>
    public static IReadOnlyList<VmAgentVersion>? Parse(string json)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonArray items) return null;
            var agents = new List<VmAgentVersion>();
            foreach (var node in items)
            {
                if (node is not System.Text.Json.Nodes.JsonObject item) return null;
                string? Text(string name) => item[name] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
                bool? signedIn = item["signedIn"] is System.Text.Json.Nodes.JsonValue b && b.TryGetValue<bool>(out var f) ? f : null;
                if (!Accept(Text("tool"), Text("version"), signedIn, agents)) return null;
            }

            return agents;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The tool ids of the coding agents a VM is meant to have (its desired settings; null: any).</summary>
    public static IReadOnlyCollection<string>? Chosen(VmDesiredState? desired) => desired is null ? null
        : (CodingAgents.FromAiTools(CodingAgents.AiTools(desired)) ?? CodingAgents.Default).Select(AgentSignInScript.Tool).ToArray();

    /// <summary>
    /// Stores what the VM reports. An agent whose check did not answer keeps what the VM said last. One that was
    /// signed in and is signed out now, and is one of <paramref name="chosen"/> (tool ids; null: any), becomes a lost
    /// sign-in; one signed in again, or gone from the VM, clears its own.
    /// </summary>
    /// <param name="busy">
    /// A job works on the VM (create, reinstall, restore, provision): a rebuilt guest reports its agents signed out until
    /// the restore brings them back, so a signed-out report changes nothing now and the first report after the job decides.
    /// </param>
    public async Task SaveAsync(Vm vm, IReadOnlyList<VmAgentVersion> agents, DateTimeOffset at, CancellationToken ct, IReadOnlyCollection<string>? chosen = null,
        bool busy = false)
    {
        await _marking.WaitAsync(ct);
        try
        {
            var before = await GetAsync(vm, ct);
            var lost = (before?.Lost ?? []).ToList();
            var now = new List<VmAgentVersion>();
            foreach (var agent in agents)
            {
                var was = before?.Agents.FirstOrDefault(a => a.Tool == agent.Tool)?.SignedIn;
                var known = agent with { SignedIn = busy && agent.SignedIn == false ? was : agent.SignedIn ?? was };
                now.Add(known);
                if (known.SignedIn == true) lost.RemoveAll(l => l.Tool == agent.Tool);
                else if (!busy && known.SignedIn == false && was == true && (chosen is null || chosen.Contains(agent.Tool)) && lost.All(l => l.Tool != agent.Tool))
                    lost.Add(new(agent.Tool, at));
            }

            // While a job holds the VM an agent the report leaves out (its check did not answer mid-provision) keeps what
            // was known of it, so a sign-in lost after the job is still told from the one before.
            if (busy && before is not null)
                now.AddRange(before.Agents.Where(a => now.All(n => n.Tool != a.Tool)));
            lost.RemoveAll(l => now.All(a => a.Tool != l.Tool));
            await WriteAsync(vm, new VmAgentsReport(vm.Created, at, now, lost), before, ct);
        }
        finally { _marking.Release(); }
    }

    public async Task<VmAgentsReport?> GetAsync(Vm vm, CancellationToken ct) =>
        await config.GetAsync<VmAgentsReport>(Key(vm.Name), ct) is { } report && report.VmCreated == vm.Created ? report : null;

    /// <summary>The owner puts a lost sign-in away. False when there was none.</summary>
    public async Task<bool> DismissAsync(Vm vm, string tool, CancellationToken ct)
    {
        await _marking.WaitAsync(ct);
        try
        {
            if (await GetAsync(vm, ct) is not { Lost: { } lost } report || lost.All(l => l.Tool != tool)) return false;
            await WriteAsync(vm, report with { Lost = lost.Where(l => l.Tool != tool).ToArray() }, report, ct);
            return true;
        }
        finally { _marking.Release(); }
    }

    /// <summary>
    /// Writes a report and announces a change of the agents (<c>vm.agents.changed</c>, R11: the pages read them again at
    /// once) and each lost sign-in that appeared or went.
    /// </summary>
    private async Task WriteAsync(Vm vm, VmAgentsReport report, VmAgentsReport? before, CancellationToken ct)
    {
        await config.SetAsync(Key(vm.Name), report, "vm:" + vm.Name, ct);
        if (before is null || !before.Agents.SequenceEqual(report.Agents)) changes.Vm("vm.agents.changed", vm);
        var was = before?.Lost ?? [];
        var now = report.Lost ?? [];
        foreach (var tool in was.Select(l => l.Tool).Except(now.Select(l => l.Tool)).Concat(now.Select(l => l.Tool).Except(was.Select(l => l.Tool))))
            changes.Publish("agent-notice.changed", tool, vm.Name, vm.Owner, ownerOnly: true);
    }

    private readonly SemaphoreSlim _marking = new(1, 1);

    /// <summary>
    /// What a sign-in on the VM found when it ended (design 11.8): the agent's sign-in state, and its version where
    /// the VM reported none yet, so the pages show it before the next report comes.
    /// </summary>
    public async Task MarkSignedInAsync(Vm vm, string tool, bool signedIn, string? version, DateTimeOffset at, CancellationToken ct)
    {
        await _marking.WaitAsync(ct);
        try
        {
            var report = await GetAsync(vm, ct);
            var agents = (report?.Agents ?? []).ToList();
            var i = agents.FindIndex(a => a.Tool == tool);
            if (i >= 0) agents[i] = agents[i] with { SignedIn = signedIn };
            else if (version is not null && VersionPattern().IsMatch(version)) agents.Add(new(tool, version, signedIn));
            else return;
            // Signed in again: its lost sign-in goes. A sign-in that did not work adds none: the user saw it fail.
            var lost = (report?.Lost ?? []).Where(l => !(signedIn && l.Tool == tool)).ToArray();
            await WriteAsync(vm, new VmAgentsReport(vm.Created, report?.ReportedAt ?? at, agents, lost), report, ct);
        }
        finally { _marking.Release(); }
    }

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
            "opencode" => ("https://registry.npmjs.org/@opencode/cli/latest", "version"), // OpenCode 2; OpenCode 1 was opencode-ai
            // The manifest Google's installer and the CLI's updater read (T3 Code reads it too).
            "antigravity" => ("https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/linux_amd64.json", "version"),
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
