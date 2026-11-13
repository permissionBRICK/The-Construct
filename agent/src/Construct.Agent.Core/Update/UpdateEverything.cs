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
/// named. The parts and their state live in memory only: there is no stored plan, and a run cut short
/// is started again by hand.
/// </summary>
public sealed class UpdateEverything(IDeviceEngines engines, AgentUpdater self, Action changed, TimeProvider time)
{
    private readonly object _gate = new();
    private List<UpdatePart> _parts = [];
    private Task _run = Task.CompletedTask;

    /// <summary>How long a host update may take, restart included, before its part is reported failed.</summary>
    public TimeSpan HostUpdateLimit { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    public bool Running
    {
        get { lock (_gate) return !_run.IsCompleted; }
    }

    public EverythingView View()
    {
        lock (_gate) return new(!_run.IsCompleted, [.. _parts]);
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
            var hostOk = await HostAsync(engine).ConfigureAwait(false);
            if (!hostOk)
            {
                Set("vms", engine.Id, "skipped", "The host update did not finish.");
                continue;
            }

            Set("vms", engine.Id, "running");
            try
            {
                var answer = await engines.PostAsync(engine.Id, "me/vms/reprovision", null, CancellationToken.None).ConfigureAwait(false);
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
                    // The device goes last: its own install restarts this agent, which ends the run.
                    Set("device", null, "running", "Installs when the agent is idle.");
                    self.StartInstall();
                }
            }
            catch (AgentFailure failure)
            {
                Set("device", null, "failed", failure.Message);
            }
        }
    }

    /// <summary>The engine's own update, when the user administers it and it offers one. False when the host update failed.</summary>
    private async Task<bool> HostAsync(EngineView engine)
    {
        Set("engine", engine.Id, "running");
        try
        {
            var who = await engines.GetAsync(engine.Id, "whoami", CancellationToken.None).ConfigureAwait(false);
            if (!string.Equals(who?["role"]?.GetValue<string>(), "admin", StringComparison.OrdinalIgnoreCase))
            {
                Set("engine", engine.Id, "skipped", "Its admin updates this host.");
                return true;
            }

            var status = await engines.GetAsync(engine.Id, "host/updates/status", CancellationToken.None).ConfigureAwait(false);
            if (status?["available"] is not JsonObject offer)
            {
                Set("engine", engine.Id, "done", "Up to date.");
                return true;
            }

            var started = await engines.PostAsync(engine.Id, "host/updates/run", null, CancellationToken.None).ConfigureAwait(false);
            if (started?["jobId"]?.GetValue<string>() is not { Length: > 0 } job)
            {
                Set("engine", engine.Id, "done", started?["state"]?.GetValue<string>());
                return true;
            }

            Set("engine", engine.Id, "running", "Updating to " + offer["version"]?.GetValue<string>());
            return await FollowAsync(engine.Id, job).ConfigureAwait(false);
        }
        catch (AgentFailure failure)
        {
            Set("engine", engine.Id, "failed", failure.Message);
            return false;
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
