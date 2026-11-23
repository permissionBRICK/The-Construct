using System.Text.Json.Nodes;
using Construct.Agent.Device;

namespace Construct.Agent.Update;

/// <summary>The new Construct on offer at a host of the old stack: its release tag and commit, and the update already staged for it, if any.</summary>
public sealed record OldHostOffer(string ReleaseTag, string Commit, string? StagedId);

/// <summary>
/// A shared host still on the old stack brought to the new Construct through its own update API, as the old Companion's
/// Update host did (<c>GET /host/updates/status</c>, <c>POST /host/updates/check</c>, <c>stage</c> with auto-apply, or
/// <c>apply</c> for one already staged): only where the user administers it and what it offers is the new version, i.e. a
/// release that ships the device agent (no release of the old stack does). A host that offers nothing new, or another
/// release of the old stack, stays as it is. Nothing here names a host.
/// </summary>
public sealed class OldHostUpdate(IDeviceEngines engines, Func<string, string, CancellationToken, Task<bool>> shipsAgent, TimeProvider time)
{
    private static readonly string[] Active = ["checking", "draining", "handedOff", "applying"];
    private static readonly string[] Terminal = ["succeeded", "cancelled", "stageFailed", "applyFailed", "rolledBack", "rolledBackWithDatabase", "resolvedByAdmin", "interrupted", "recoveryFailed"];

    /// <summary>How long the update may take, restart included.</summary>
    public TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The new Construct the host offers, or null: the user is not its admin, it offers nothing or another release of the
    /// old stack, it cannot take the update, or an update of its admin's is under way. A failure to ask is null too.
    /// </summary>
    public async Task<OldHostOffer?> OfferAsync(string engine, CancellationToken ct)
    {
        try
        {
            // Admin only: anyone else is refused here.
            var status = await engines.GetAsync(engine, "host/updates/status", ct).ConfigureAwait(false);
            // A host from before auto-apply is on its own (installs not updated since mid-September 2026).
            if (status?["supportsAutoApply"] is not JsonValue auto || !auto.TryGetValue<bool>(out var supported) || !supported) return null;
            var current = status["current"] as JsonObject;
            var state = Text(current?["state"]);
            if (state == "staged")
            {
                var stagedCommit = Text(current!["commit"]);
                return stagedCommit is { Length: 40 } && Text(current["updateId"]) is { } id && await NewVersionAsync(engine, "host-" + stagedCommit, ct).ConfigureAwait(false)
                    ? new OldHostOffer("host-" + stagedCommit, stagedCommit, id)
                    : null;
            }

            if (state is not null && Active.Contains(state, StringComparer.Ordinal)) return null;
            var check = await engines.PostAsync(engine, "host/updates/check", null, ct).ConfigureAwait(false);
            if (check?["latest"] is not JsonObject latest || Text(latest["releaseTag"]) is not { } tag || Text(latest["commit"]) is not { } commit) return null;
            if (string.Equals(commit, Text(check["installed"]?["commit"]), StringComparison.OrdinalIgnoreCase)) return null;
            if (latest["compatible"] is JsonValue compatible && compatible.TryGetValue<bool>(out var ok) && !ok) return null;
            return await NewVersionAsync(engine, tag, ct).ConfigureAwait(false) ? new OldHostOffer(tag, commit, null) : null;
        }
        catch (AgentFailure)
        {
            return null;
        }
    }

    /// <summary>Whether the release is the new Construct; the release comes from the repository the host updates from.</summary>
    private async Task<bool> NewVersionAsync(string engine, string tag, CancellationToken ct)
    {
        var repository = Text((await engines.GetAsync(engine, "host/config", ct).ConfigureAwait(false))?["updates"]?["repository"]) ?? AgentUpdater.DefaultRepository;
        try { return await shipsAgent(repository, tag, ct).ConfigureAwait(false); }
        catch (AgentFailure) { return false; }
    }

    /// <summary>
    /// Starts the update (staged with auto-apply, or the staged one applied) and waits until the host answers as the new
    /// stack (<c>GET /engine</c>) and is healthy with the offered commit; null then, otherwise what went wrong. The new
    /// stack answers already while it finishes the update (maintenance) and may still roll back then: that is a failure.
    /// The host restarts in between: no answer is waited out. <paramref name="progress"/> sees how far it is (a percentage that
    /// only grows) and a word: updating, restarting, finishing.
    /// </summary>
    public async Task<string?> RunAsync(string engine, OldHostOffer offer, CancellationToken ct, Action<int, string>? progress = null)
    {
        var shown = 0;
        void Report(int percent, string word)
        {
            shown = Math.Max(shown, Math.Clamp(percent, 0, 99));
            progress?.Invoke(shown, word);
        }

        Report(0, "Updating");
        var key = "construct-agent-" + Guid.NewGuid().ToString("N");
        string updateId;
        try
        {
            if (offer.StagedId is { } staged)
            {
                updateId = staged;
                await engines.PostAsync(engine, "host/updates/apply", new JsonObject { ["updateId"] = staged, ["operationKey"] = key + "-apply" }, ct).ConfigureAwait(false);
            }
            else
            {
                var answer = await engines.PostAsync(engine, "host/updates/stage",
                    new JsonObject { ["releaseTag"] = offer.ReleaseTag, ["operationKey"] = key, ["autoApply"] = true }, ct).ConfigureAwait(false);
                if (Text(answer?["updateId"]) is not { } id) return "Started no update";
                updateId = id;
            }
        }
        catch (AgentFailure failure)
        {
            return $"Update not started ({failure.Status})";
        }

        var started = time.GetUtcNow();
        var until = started + Limit;
        while (time.GetUtcNow() < until)
        {
            await Task.Delay(PollInterval, time, ct).ConfigureAwait(false);
            try
            {
                await engines.GetAsync(engine, "engine", ct).ConfigureAwait(false);
                if (await HealthyAsync(engine, offer.Commit, ct).ConfigureAwait(false)) return null;
                Report(85, "Finishing");
                continue; // still finishing the update
            }
            catch (AgentFailure failure) when (failure.Status == 404 && failure.Code != "no-engine")
            {
                // Still the old stack, or again after a rollback: its own status says how the update went. The bar creeps
                // towards half while it downloads and stages (a few minutes).
                var minutes = (time.GetUtcNow() - started).TotalMinutes;
                Report(5 + (int)(45 * minutes / (minutes + 3)), "Updating");
            }
            catch (AgentFailure)
            {
                Report(60, "Restarting");
                continue; // restarting
            }

            try
            {
                var current = (await engines.GetAsync(engine, "host/updates/status", ct).ConfigureAwait(false))?["current"];
                if (Text(current?["updateId"]) == updateId && Text(current?["state"]) is { } state && Terminal.Contains(state, StringComparer.Ordinal))
                    return state == "succeeded" ? "Updated, still the old Construct" : $"Update {state}; previous version kept";
            }
            catch (AgentFailure)
            {
            }
        }

        return "Update did not finish in time";
    }

    /// <summary>Whether the host answers ok with <paramref name="commit"/>; as <see cref="UpdateEverything"/> checks, the ok of one that does not say its commit is taken.</summary>
    private async Task<bool> HealthyAsync(string engine, string commit, CancellationToken ct)
    {
        try
        {
            var health = await engines.GetAsync(engine, "health", ct).ConfigureAwait(false);
            if (Text(health?["status"]) != "ok") return false;
            return Text(health?["commit"]) is not { } running || string.Equals(running, commit, StringComparison.OrdinalIgnoreCase);
        }
        catch (AgentFailure)
        {
            return false;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}
