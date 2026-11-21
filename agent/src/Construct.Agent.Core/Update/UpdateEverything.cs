using System.Text.Json.Nodes;
using Construct.Agent.Device;

namespace Construct.Agent.Update;

/// <param name="Part"><c>engine</c> (the host update), <c>vms</c> (the user's own VMs there) or <c>device</c>.</param>
/// <param name="State"><c>pending</c>, <c>running</c>, <c>done</c>, <c>skipped</c> or <c>failed</c>.</param>
public sealed record UpdatePart(string Part, string? Engine, string Name, string State, string? Detail = null);

public sealed record EverythingView(bool Running, IReadOnlyList<UpdatePart> Parts);

/// <summary>
/// "Update everything" (12.1, S28): each engine the user administers runs its own update
/// (<c>POST /host/updates/run</c>, only when it offers one), each engine reprovisions the user's own
/// VMs (<c>POST /me/vms/reprovision</c>, which an engine scopes to its caller), and then this device
/// updates itself. A host the user does not administer is not asked to update; nobody else's VM is
/// named. A host that still runs the old Construct (no <c>GET /engine</c>) is left out with a note:
/// it is updated from its own host page. After a host update the VM part waits until the restarted
/// engine answers healthy. The parts and their state live in memory only: there is no stored plan,
/// and a run cut short is started again by hand.
/// </summary>
public sealed class UpdateEverything(IDeviceEngines engines, AgentUpdater self, Action changed, TimeProvider time)
{
    private readonly object _gate = new();
    private List<UpdatePart> _parts = [];
    private Task _run = Task.CompletedTask;

    /// <summary>How long a host update may take, restart included, before its part is reported failed.</summary>
    public TimeSpan HostUpdateLimit { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the VM part waits for an engine to answer healthy and to take the reprovision (it restarted, or is busy starting).</summary>
    public TimeSpan ReadyLimit { get; init; } = TimeSpan.FromMinutes(5);

    public const string OldStackNote = "Runs the old Construct; update it from its host page.";

    public bool Running
    {
        get { lock (_gate) return !_run.IsCompleted; }
    }

    public EverythingView View()
    {
        UpdatePart[] parts;
        bool running;
        lock (_gate)
        {
            parts = [.. _parts];
            running = !_run.IsCompleted;
        }

        // The device's own updater is asked outside this lock: it asks Running in turn.
        return new(running, [.. parts.Select(Live)]);
    }

    /// <summary>The device part while it runs says what the device's own update does now (12.1, finding 21).</summary>
    private UpdatePart Live(UpdatePart part)
    {
        if (part.Part != "device" || part.State != "running") return part;
        var view = self.View();
        return view.State switch
        {
            "waiting" => part with { Detail = view.Waiting is { } what ? $"Waits for {what} to end." : "Starts in a moment." },
            "downloading" => part with { Detail = "Downloading." },
            "installing" => part with { Detail = "Installing; Construct restarts by itself." },
            "failed" => part with { State = "failed", Detail = view.Error },
            _ => part,
        };
    }

    public Task Work
    {
        get { lock (_gate) return _run; }
    }

    public EverythingView Start()
    {
        lock (_gate)
        {
            if (!_run.IsCompleted) return View();
            var list = engines.List();
            _parts = [.. list.SelectMany(e => new[] { new UpdatePart("engine", e.Id, e.Name, "pending"), new UpdatePart("vms", e.Id, e.Name, "pending") }),
                new UpdatePart("device", null, "This device", "pending")];
            _run = RunAsync(list);
        }

        changed();
        return View();
    }

    private async Task RunAsync(IReadOnlyList<EngineView> list)
    {
        await Task.Yield();
        foreach (var engine in list)
        {
            if (engine.EngineId is null && await OldStackAsync(engine.Id).ConfigureAwait(false))
            {
                // Not an update target: one line for the host, and its VMs are not named.
                Set("engine", engine.Id, "skipped", OldStackNote);
                lock (_gate) _parts = [.. _parts.Where(p => !(p.Part == "vms" && p.Engine == engine.Id))];
                changed();
                continue;
            }

            var host = await HostAsync(engine).ConfigureAwait(false);
            if (host == HostResult.Failed)
            {
                Set("vms", engine.Id, "skipped", "The host update did not finish.");
                continue;
            }

            Set("vms", engine.Id, "running", host == HostResult.Updated ? "Waits for the host to come back." : null);
            try
            {
                if (host == HostResult.Updated && !await HealthyAsync(engine.Id).ConfigureAwait(false))
                {
                    Set("vms", engine.Id, "failed", "The host did not answer after its update; reprovision the VMs from their pages.");
                    continue;
                }

                Set("vms", engine.Id, "running");
                var answer = await ReprovisionAsync(engine.Id).ConfigureAwait(false);
                var count = (answer?["vms"] as JsonArray)?.Count ?? 0;
                Set("vms", engine.Id, "done", count == 0 ? "No VMs of yours here." : $"{count} VM{(count == 1 ? "" : "s")} reprovisioning one after the other.");
            }
            catch (AgentFailure failure)
            {
                Set("vms", engine.Id, "failed", failure.Message);
            }
        }

        if (self.Running is null) Set("device", null, "skipped", "This agent does not run from an installed package.");
        else
        {
            try
            {
                await self.CheckAsync(CancellationToken.None).ConfigureAwait(false);
                if (!self.Available) Set("device", null, "done", "Up to date.");
                else
                {
                    // The device goes last: its own install restarts this agent, which ends the run. While it runs, the
                    // part says what the install does (Live).
                    Set("device", null, "running");
                    self.StartInstall();
                }
            }
            catch (AgentFailure failure)
            {
                Set("device", null, "failed", failure.Message);
            }
        }
    }

    private enum HostResult { Unchanged, Updated, Failed }

    /// <summary>Whether the engine runs the old stack: it has no <c>GET /engine</c> (404). Unreachable is not old.</summary>
    private async Task<bool> OldStackAsync(string engine)
    {
        try
        {
            await engines.GetAsync(engine, "engine", CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (AgentFailure failure) { return failure.Status == 404; }
    }

    /// <summary>The engine's own update, when the user administers it and it offers one.</summary>
    private async Task<HostResult> HostAsync(EngineView engine)
    {
        Set("engine", engine.Id, "running");
        try
        {
            var who = await engines.GetAsync(engine.Id, "whoami", CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(who?["role"]?.GetValue<string>(), "admin", StringComparison.OrdinalIgnoreCase))
            {
                Set("engine", engine.Id, "skipped", "Its admin updates this host.");
                return HostResult.Unchanged;
            }

            var status = await engines.GetAsync(engine.Id, "host/updates/status", CancellationToken.None).ConfigureAwait(false);
            if (status?["available"] is not JsonObject offer)
            {
                Set("engine", engine.Id, "done", "Up to date.");
                return HostResult.Unchanged;
            }

            var started = await engines.PostAsync(engine.Id, "host/updates/run", null, CancellationToken.None).ConfigureAwait(false);
            if (started?["jobId"]?.GetValue<string>() is not { Length: > 0 } job)
            {
                Set("engine", engine.Id, "done", started?["state"]?.GetValue<string>());
                return HostResult.Unchanged;
            }

            Set("engine", engine.Id, "running", "Updating to " + offer["version"]?.GetValue<string>());
            return await FollowAsync(engine.Id, job).ConfigureAwait(false) ? HostResult.Updated : HostResult.Failed;
        }
        catch (AgentFailure failure)
        {
            Set("engine", engine.Id, "failed", failure.Message);
            return HostResult.Failed;
        }
    }

    /// <summary>
    /// After its update the engine restarts and finishes the update first: until <c>GET /health</c> says <c>ok</c>
    /// it answers 503 (finding 20). True once it does, false when it does not within <see cref="ReadyLimit"/>.
    /// </summary>
    private async Task<bool> HealthyAsync(string engine)
    {
        var until = time.GetUtcNow() + ReadyLimit;
        while (true)
        {
            try
            {
                if ((await engines.GetAsync(engine, "health", CancellationToken.None).ConfigureAwait(false))?["status"]?.GetValue<string>() == "ok") return true;
            }
            catch (AgentFailure) { /* restarting: asked again */ }

            if (time.GetUtcNow() >= until) return false;
            await Task.Delay(PollInterval, time).ConfigureAwait(false);
        }
    }

    /// <summary>The user's VMs reprovisioned; an engine that answers 5xx (still starting) is asked again within <see cref="ReadyLimit"/>.</summary>
    private async Task<JsonNode?> ReprovisionAsync(string engine)
    {
        var until = time.GetUtcNow() + ReadyLimit;
        while (true)
        {
            try
            {
                return await engines.PostAsync(engine, "me/vms/reprovision", null, CancellationToken.None).ConfigureAwait(false);
            }
            // An answer of 5xx means the engine did not take it; no answer at all might have.
            catch (AgentFailure failure) when (failure.Status is 502 or 503 && failure.Code != "engine-unreachable" && time.GetUtcNow() < until)
            {
                await Task.Delay(PollInterval, time).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Polls the host update's job; the engine restarts in between, so a missing answer is waited out.</summary>
    private async Task<bool> FollowAsync(string engine, string job)
    {
        var until = time.GetUtcNow() + HostUpdateLimit;
        while (time.GetUtcNow() < until)
        {
            await Task.Delay(PollInterval, time).ConfigureAwait(false);
            try
            {
                var state = (await engines.GetAsync(engine, "jobs/" + Uri.EscapeDataString(job), CancellationToken.None).ConfigureAwait(false))?["state"]?.GetValue<string>();
                switch (state)
                {
                    case "succeeded":
                        Set("engine", engine, "done");
                        return true;
                    case "failed" or "cancelled":
                        Set("engine", engine, "failed", "The host update " + state + "; the host runs its previous version.");
                        return false;
                }
            }
            catch (AgentFailure failure) when (failure.Code is "engine-unreachable" or "not-found" || failure.Status is 404 or 502 or 503)
            {
            }
        }

        Set("engine", engine, "failed", "The host update did not finish in time.");
        return false;
    }

    private void Set(string part, string? engine, string state, string? detail = null)
    {
        lock (_gate) _parts = [.. _parts.Select(p => p.Part == part && p.Engine == engine ? p with { State = state, Detail = detail } : p)];
        changed();
    }
}
