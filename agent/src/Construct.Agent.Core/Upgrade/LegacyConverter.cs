using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Update;

namespace Construct.Agent.Upgrade;

/// <summary>
/// The converter (13.1): it reads the old client's state (<see cref="LegacyState"/>) and copies it into the
/// new stack, writing only to the engines, the store and the agent's own files (<c>bridge.json</c>,
/// <c>device.json</c>). It changes nothing of the old files until the move is complete;
/// then the old client's autoinstall images go and its entry scripts are replaced. It can be run
/// again at any point: an item listed as done in <c>bridge.json</c>, or already present where it goes, is
/// left alone. A shared host of the old stack that the user administers moves to the new version through its own update
/// when it offers it (R5). Its last steps switch the local VMs over to the engine (the adopt job, which starts a VM that is
/// off) and reprovision each adopted VM with the engine's guest payload, and each moved host's VMs (after handing the
/// engine there the old client's key for them), so the move leaves them on the new version; then
/// <c>{stage: converted}</c> is written, and only after that the old backup key comes off the VMs, the old client's
/// autoinstall images are deleted and its entry scripts become the new Construct's (<see cref="OldInstallScripts"/>).
/// Messages are a few words (R6).
/// </summary>
public sealed class LegacyConverter(DeviceAgent agent, IDeviceEngines engines, DeviceSettings settings, BridgeFile bridge,
    IUpgradePlatform platform, Action<UpgradeItem> progress)
{
    public const string RecipientLabel = "construct-account";

    /// <summary>Called before each item with its id; tests throw here to cut a run short.</summary>
    public Func<string, Task>? BeforeItem { get; init; }

    /// <summary>How long one adoption job may take.</summary>
    public TimeSpan AdoptTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long the reprovision of one adopted VM may take.</summary>
    public TimeSpan ReprovisionTimeout { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>How long a VM on a moved host that had to be started may boot before the old client's key reaches it.</summary>
    public TimeSpan StartedVmWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The pause between two tries to hand the old client's key to a VM that boots.</summary>
    public TimeSpan StartedVmPoll { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Moves a shared host of the old stack through its own update (R5); null: none is moved.</summary>
    public OldHostUpdate? OldHosts { get; init; }

    /// <summary>The whole run's progress for the dialog's one bar (R7).</summary>
    public Action<StepProgress>? Overall { get; init; }

    private readonly List<UpgradeItem> _items = [];
    private HashSet<string> _done = [];
    private int _percent;
    private string _label = "Starting";

    /// <summary>Runs every item; true when the conversion is complete (<c>converted</c>).</summary>
    public async Task<bool> RunAsync(LegacyState legacy, CancellationToken ct)
    {
        if (!agent.IsRegistered || agent.RecoveryUnconfirmed) throw new AgentFailure(409, "not-registered", "Set up the recovery code first: the converted records are sealed under the account's keys.");
        var start = bridge.Read();
        // A converter before this one marked "host-vms:<host>" done right after asking a moved host to reprovision, without
        // knowing whether it did (it could not: the engine there did not reach the old host's VMs). That row is
        // "host-reprovision:<host>" now, and it runs again with the old client's keys; the old mark goes.
        if (start.Done.Any(StaleHostVms))
        {
            bridge.Update(b => b with { Done = b.Done.Where(d => !StaleHostVms(d)).ToArray() });
            start = bridge.Read();
        }

        _done = start.Done.ToHashSet(StringComparer.Ordinal);
        var store = start.StoreEngine ?? agent.StoreEngineId ?? throw new AgentFailure(409, "no-store", "This device has no store yet.");
        bridge.Update(b => b with { Stage = b.Stage == BridgeState.Converted ? b.Stage : "converting", From = legacy.InstallFolder, Items = [] });

        var instances = legacy.Instances();
        // The bar (R7): the copying is quick; where VMs of this PC move, their switch-over and above all their
        // reprovision take most of the time.
        var vms = start.LocalEngine is null ? 0 : instances.Count(i => i.Local);
        var copy = vms == 0 ? 95.0 : 10.0;
        Func<Task>[] copying =
        [
            () => ChannelAsync(legacy, ct), () => HostsAsync(legacy, ct), () => ProfilesAsync(legacy, store, ct), () => UpstreamAsync(legacy, store, ct),
            () => SecretsAsync(legacy, ct),
        ];
        for (var i = 0; i < copying.Length; i++)
        {
            Progress(copy * i / (copying.Length + 2));
            await copying[i]().ConfigureAwait(false);
        }

        Progress(copy * copying.Length / (copying.Length + 2));
        var legacyKeys = await BackupKeysAsync(legacy, ct).ConfigureAwait(false);
        Progress(copy * (copying.Length + 1) / (copying.Length + 2));
        await RecipientsAsync(legacy, instances.Where(i => !i.Local), start.LocalEngine, ct).ConfigureAwait(false);
        Progress(copy);
        await LocalVmsAsync(legacy, instances, start.LocalEngine, copy, ct).ConfigureAwait(false);
        // A local VM's account key after its switch-over, when the engine runs it (and it runs: the adoption starts a VM
        // that is off); before it, the VM may be off and nothing reaches it.
        await RecipientsAsync(legacy, instances.Where(i => i.Local), start.LocalEngine, ct).ConfigureAwait(false);
        await ReprovisionAsync(instances, start.LocalEngine, ct).ConfigureAwait(false);
        await HostVmsAsync(legacy, instances, ct).ConfigureAwait(false);

        var complete = _items.All(i => i.State is not "failed");
        if (complete && bridge.Read().Stage != BridgeState.Converted)
            bridge.Update(b => b with { Stage = BridgeState.Converted, ConvertedAt = DateTimeOffset.UtcNow });
        if (bridge.Read().Stage == BridgeState.Converted)
        {
            await RemoveLegacyRecipientsAsync(legacy, instances, legacyKeys, start.LocalEngine, ct).ConfigureAwait(false);
            DeleteInstallImages(legacy);
            // One that could not be replaced is named in the notes and tried again (UpgradeService).
            OldInstallScripts.Protect(legacy, bridge, agent.Account);
        }

        if (complete) Progress(100);
        bridge.Update(b => b with { Items = _items.ToArray() });
        return complete;
    }

    /// <summary>The bar moves to <paramref name="percent"/>, never back, named by the running row.</summary>
    private static bool StaleHostVms(string id) => id.StartsWith("host-vms:", StringComparison.Ordinal);

    private void Progress(double percent)
    {
        _percent = Math.Max(_percent, (int)Math.Round(Math.Clamp(percent, 0, 100)));
        Overall?.Invoke(new StepProgress(_percent, _label));
    }

    /// <summary>
    /// A note for the user, saved at once: an item that wrote one is skipped by a later run, which would not say it
    /// again. Notes of earlier runs stay (they are facts about the old install), until the user read them.
    /// </summary>
    private void Note(string text)
    {
        bridge.Update(b => b.Notes.Contains(text) && b.NotesAccount == agent.Account
            ? b
            : b with { Notes = b.NotesAccount == agent.Account ? [.. b.Notes, text] : [text], NotesAccount = agent.Account, NotesRead = false });
    }

    // ---- one item ----

    private async Task ItemAsync(string id, string label, Func<Task<(string State, string? Message)>> work, bool remember = true, string? running = null)
    {
        if (_done.Contains(id))
        {
            Report(new UpgradeItem(id, label, "skipped", "Done earlier"));
            return;
        }

        Report(new UpgradeItem(id, label, "running", running));
        if (BeforeItem is not null) await BeforeItem(id).ConfigureAwait(false);
        UpgradeItem item;
        try
        {
            var (state, message) = await work().ConfigureAwait(false);
            item = new UpgradeItem(id, label, state, message);
        }
        catch (AgentFailure failure)
        {
            item = new UpgradeItem(id, label, "failed", failure.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or TaskCanceledException or UnauthorizedAccessException)
        {
            // The type only: a message could carry a path or an answer, never what an item holds.
            item = new UpgradeItem(id, label, "failed", $"Unexpected failure ({ex.GetType().Name}).");
        }

        if (remember && item.State is "done" or "skipped") Remember(id);
        Report(item);
    }

    /// <summary>Saves <paramref name="id"/> as done at once: a later run leaves it alone.</summary>
    private void Remember(string id)
    {
        _done.Add(id);
        bridge.Update(b => b with { Done = b.Done.Contains(id) ? b.Done : [.. b.Done, id] });
    }

    /// <summary>
    /// Names the items of a group before the first of them runs, as waiting: the dialog shows one row per group with
    /// "n / total" (finding 6), and the total is known from the start.
    /// </summary>
    private void Waiting(IEnumerable<(string Id, string Label)> items)
    {
        foreach (var (id, label) in items) Report(new UpgradeItem(id, label, "waiting"));
    }

    private void Report(UpgradeItem item)
    {
        if (item.State == "running")
        {
            _label = item.Label;
            Progress(_percent);
        }

        var at = _items.FindIndex(i => i.Id == item.Id);
        if (at >= 0) _items[at] = item;
        else _items.Add(item);
        progress(item);
    }

    // ---- settings ----

    /// <summary><c>constructRef</c> becomes the device channel (12A.5); a device already on nightly (moved by hand to a branch) stays there.</summary>
    private Task ChannelAsync(LegacyState legacy, CancellationToken ct) => ItemAsync("channel", "Release channel", () =>
    {
        var channel = legacy.Channel();
        var current = settings.Read().Channel;
        if (current != "stable" || current == channel) return Task.FromResult<(string, string?)>(("done", $"Stays {current}"));
        settings.Update(s => s with { Channel = channel });
        return Task.FromResult<(string, string?)>(("done", $"Now {channel}"));
    });

    // ---- remote hosts ----

    private async Task HostsAsync(LegacyState legacy, CancellationToken ct)
    {
        foreach (var host in legacy.Hosts())
        {
            var name = new Uri(host.Url).Host;
            OldHostOffer? offer = null;
            var asked = false;
            await ItemAsync("host:" + host.Slug, "Host " + name, async () =>
            {
                if (host.Pin is null) return ("failed", "No pinned certificate; add it in Settings");
                var known = agent.Engines().FirstOrDefault(e => e.Id == agent.EngineIdFor(host.Url));
                // The old token is read only when this device has no credential there yet; it goes into the state protected.
                var token = known is null || !known.Enrolled && !known.SignedIn ? (host.HasToken ? legacy.HostToken(host) : null) : null;
                string? problem = null;
                try { known = await agent.AddEngineAsync(host.Url, host.Pin, null, token, ct).ConfigureAwait(false); }
                catch (AgentFailure failure) { problem = failure.Message; }

                // Added even when connecting failed: the engine entry is saved before the connection is tried.
                known = agent.Engines().FirstOrDefault(e => e.Id == agent.EngineIdFor(host.Url));
                if (known is null) return ("failed", problem ?? "The host could not be added.");
                if (!known.Enrolled || host.VaultMode is not null)
                {
                    try { known = await agent.ConnectEngineAsync(known.Id, host.VaultMode ?? "locked", ct).ConfigureAwait(false); }
                    catch (AgentFailure failure) { problem = failure.Message; }
                }

                if (await UpgradeService.MarkOldHostAsync(agent, bridge, known.Id, ct).ConfigureAwait(false) == true)
                {
                    // The old stack, kept with the old sign-in: it moves next when its user administers it and it offers the new version.
                    asked = true;
                    offer = OldHosts is null ? null : await OldHosts.OfferAsync(known.Id, ct).ConfigureAwait(false);
                    return ("done", offer is null ? "Old Construct, unchanged" : "Old Construct; moves next");
                }

                if (known.Enrolled) return ("done", host.VaultMode is null ? "Enrolled" : $"Enrolled, vault {host.VaultMode}");
                // Carried over all the same: the host keeps its pin and the old sign-in, and connecting it again from
                // Settings enrols this device once the host can. A second run leaves it alone.
                return ("done", known.SignedIn ? "Signed in; connect it in Settings" : "Sign in from Settings");
            }).ConfigureAwait(false);

            // Added in an earlier run and still on the old stack: it may offer the new version by now.
            if (!asked && OldHosts is not null && !_done.Contains("host-update:" + host.Slug) && agent.EngineIdFor(host.Url) is { } id
                && await agent.RunsOldStackAsync(id, ct).ConfigureAwait(false) == true)
                offer = await OldHosts.OfferAsync(id, ct).ConfigureAwait(false);
            if (offer is not null) await HostUpdateAsync(host, name, offer, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The start of the note that names a host whose update did not go through.</summary>
    public static string HostNote(string name) => $"Host {name} not moved: ";

    /// <summary>
    /// R5: a shared host of the old stack moves to the new version through its own update, and this device connects there
    /// as at a new host, so the account key reaches its VMs through it and its VMs are reprovisioned at the end. An update
    /// that did not go through is this row's alone (pending): the move goes on, a note says so (the converted overview
    /// shows no rows), and Update tries it again.
    /// </summary>
    private Task HostUpdateAsync(LegacyHost host, string name, OldHostOffer offer, CancellationToken ct) =>
        ItemAsync("host-update:" + host.Slug, $"Host {name} on the new version", async () =>
        {
            var engine = agent.EngineIdFor(host.Url) ?? throw new AgentFailure(404, "no-engine", "The host is not added.");
            if (await OldHosts!.RunAsync(engine, offer, ct).ConfigureAwait(false) is { } problem)
            {
                bridge.Update(b => b.Noted(agent.Account, HostNote(name), HostNote(name) + problem + ". Update tries again."));
                return ("pending", problem);
            }

            bridge.Update(b => b.Noted(agent.Account, HostNote(name), null));
            EngineView known;
            try { known = await agent.ConnectEngineAsync(engine, host.VaultMode ?? "locked", ct).ConfigureAwait(false); }
            catch (AgentFailure) { known = agent.Engines().First(e => e.Id == engine); }
            await UpgradeService.MarkOldHostAsync(agent, bridge, engine, ct).ConfigureAwait(false);
            return ("done", known.Enrolled ? "Moved, enrolled" : known.SignedIn ? "Moved, signed in" : "Moved; sign in from Settings");
        }, running: "Updating");

    /// <summary>
    /// R5: the user's VMs on each host on the new version (moved by this device, in this run or an earlier one, or already
    /// moved by its admin), reprovisioned one after the other and waited for, as the adopted VMs are; the row shows the
    /// running job and fails when one of them does. A host still on the old stack, or not reached, is left out. The old
    /// host never dialled its guests (the old client on the PC provisioned them as root with its own key per VM), so the
    /// engine there reaches none of them until it has that key: it goes to the engine first, for each VM the old client
    /// here knows, with the old client's settings for it. A VM done is remembered on its own, so Move over again redoes only
    /// the ones that failed.
    /// </summary>
    private async Task HostVmsAsync(LegacyState legacy, IReadOnlyList<LegacyInstance> instances, CancellationToken ct)
    {
        foreach (var host in legacy.Hosts())
        {
            // Its admin may have moved it before this device came along: its VMs of this user need the old key just the same.
            if (!_done.Contains("host-update:" + host.Slug) &&
                (agent.EngineIdFor(host.Url) is not { } known || await agent.RunsOldStackAsync(known, ct).ConfigureAwait(false) != false))
                continue;
            var id = "host-reprovision:" + host.Slug;
            var label = $"VMs on {new Uri(host.Url).Host} on the new version";
            await ItemAsync(id, label, async () =>
            {
                var engine = agent.EngineIdFor(host.Url) ?? throw new AgentFailure(404, "no-engine", "The host is not added.");
                var me = (await engines.GetAsync(engine, "whoami", ct).ConfigureAwait(false))?["name"]?.GetValue<string>() ?? "";
                var mine = ((await engines.GetAsync(engine, "vms?kind=primary", ct).ConfigureAwait(false)) as JsonArray ?? [])
                    .Where(v => string.Equals(Text(v, "owner"), me, StringComparison.OrdinalIgnoreCase) && v?["deleting"]?.GetValue<bool>() != true)
                    .Select(v => Text(v, "name")).OfType<string>().Order(StringComparer.Ordinal).ToArray();
                if (mine.Length == 0) return ("done", "None of yours");

                var failed = new List<string>();
                var from = _percent;
                for (var i = 0; i < mine.Length; i++)
                {
                    var (vm, at) = (mine[i], i);
                    var done = $"{id}/{vm}";
                    if (_done.Contains(done)) continue;
                    void Running(int percent, string step)
                    {
                        Progress(from + (99 - from) * (at + percent / 100.0) / mine.Length);
                        Report(new UpgradeItem(id, label, "running", mine.Length == 1 ? step : $"{vm}: {step}", (100 * at + percent) / mine.Length));
                    }

                    var instance = instances.FirstOrDefault(x => !x.Local && x.Name == vm && x.ServiceUrl is { } url && agent.EngineIdFor(url) == engine);
                    Running(0, "Handing over the old key");
                    string? problem;
                    try
                    {
                        problem = instance is null ? null : await HandOverKeyAsync(legacy, instance, engine, ct).ConfigureAwait(false);
                        problem ??= await ReprovisionVmAsync(engine, vm, Running, ct).ConfigureAwait(false);
                    }
                    catch (AgentFailure failure)
                    {
                        // This VM's alone: the next one still runs.
                        problem = failure.Message;
                    }

                    if (problem is null) Remember(done);
                    else failed.Add($"{vm}: {problem}");
                }

                // The VMs kept running; Move over again retries the ones that failed.
                if (failed.Count > 0) return ("failed", string.Join("; ", failed));
                return ("done", mine.Length == 1 ? "On the new version" : $"{mine.Length} on the new version");
            }, running: "Starting").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hands the old client's root key for <paramref name="instance"/> (and its settings, for an engine that stores none) to
    /// the engine on its host, which keeps it only once it reached the VM as root, and keeps a key it has that works. Null
    /// when the engine has it or reaches the VM anyway, else why not. A VM that is off is started first, as its reprovision
    /// would be, and the key goes again while it boots. An engine without the route goes on: the reprovision says.
    /// </summary>
    private async Task<string?> HandOverKeyAsync(LegacyState legacy, LegacyInstance instance, string engine, CancellationToken ct)
    {
        var keyFile = legacy.KeyFile(instance);
        if (!File.Exists(keyFile)) return null;
        var vm = Uri.EscapeDataString(instance.Name);
        var body = new JsonObject
        {
            ["sshPrivateKey"] = File.ReadAllText(keyFile), ["settings"] = instance.Settings?.DeepClone(), ["instanceState"] = instance.InstanceState?.DeepClone(),
        };
        try
        {
            var refused = await SendAsync().ConfigureAwait(false);
            if (refused?.Code == "vm-not-running")
            {
                try { await engines.PostAsync(engine, $"vms/{vm}/power", new JsonObject { ["action"] = "start" }, ct).ConfigureAwait(false); }
                catch (AgentFailure failure) { return $"Not started ({failure.Code})"; }
                var until = DateTimeOffset.UtcNow + StartedVmWait;
                do
                {
                    await Task.Delay(StartedVmPoll, ct).ConfigureAwait(false);
                    refused = await SendAsync().ConfigureAwait(false);
                }
                // While it boots the engine may not reach it yet, or take a while trying.
                while (refused?.Code is "key-rejected" or "vm-not-running" or "vm-endpoint-unknown" or "vm-busy" or "engine-unreachable" && DateTimeOffset.UtcNow < until);
            }

            return refused switch
            {
                null or { Status: 404 } or { Code: "unsupported-capability" } => null,
                { Code: "key-rejected" } => "The old client's key does not reach it",
                { Code: "vm-not-running" } => "It does not run",
                { Code: "vm-endpoint-unknown" } => "The host has no address for it",
                _ => $"Old key not taken ({refused.Code})",
            };
        }
        finally
        {
            body["sshPrivateKey"] = null;
        }

        async Task<AgentFailure?> SendAsync()
        {
            try
            {
                await engines.SendAsync(engine, HttpMethod.Put, $"vms/{vm}/old-client-key", body, ct).ConfigureAwait(false);
                return null;
            }
            catch (AgentFailure failure)
            {
                return failure;
            }
        }
    }

    private static string? Text(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    // ---- profiles (9.4) ----

    private async Task ProfilesAsync(LegacyState legacy, string store, CancellationToken ct)
    {
        var repo = new LegacyConfigRepo(legacy.ConfigRepository);
        if (!repo.Exists) return;
        var profiles = repo.Profiles();
        Waiting(profiles.Keys.Select(name => ("profile:" + name, "Profile " + name)));
        foreach (var (name, text) in profiles)
        {
            await ItemAsync("profile:" + name, "Profile " + name, async () =>
            {
                if (Parse(text) is not { } profile) return ("skipped", "Not JSON; left as is");
                if (await ProfileAsync(store, name, ct).ConfigureAwait(false) is not null) return ("skipped", "Already there");
                try { return ("done", Status(await PutProfileAsync(store, name, profile, null, ct).ConfigureAwait(false))); }
                catch (AgentFailure failure) when (failure.Status == 400) { return ("skipped", "Invalid; left as is"); }
            }).ConfigureAwait(false);
        }

        if (!repo.IsGit) Note("The config folder is no git repository (or git is missing): VM branches were not looked at.");
        foreach (var change in repo.UnmergedChanges(legacy.Instances().Select(i => i.ConfigBranch)))
        {
            await ItemAsync($"branch:{change.Branch}:{change.Profile}", $"Unmerged change to {change.Profile} on {change.Branch}", async () =>
            {
                var ours = change.Ours is null ? null : Parse(change.Ours);
                var @base = change.Base is null ? null : Parse(change.Base);
                var theirs = change.Theirs is null ? null : Parse(change.Theirs);
                if (change.Ours is not null && ours is null || change.Base is not null && @base is null || change.Theirs is not null && theirs is null)
                    return ("skipped", "Not JSON; left as is");
                try { return ("done", await BranchChangeAsync(store, change.Profile, @base, ours, theirs, ct).ConfigureAwait(false)); }
                catch (AgentFailure failure) when (failure.Status == 400) { return ("skipped", "Invalid; left as is"); }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A VM branch's change as the VM's own write with its merge base (9.6): the store holds main; the merge base is
    /// written and main again on top, then the branch's content with the merge base as its base, so the engine
    /// merges per field and opens a conflict where both changed the same field (branch = ours, main = theirs).
    /// </summary>
    private async Task<string> BranchChangeAsync(string store, string name, JsonObject? @base, JsonObject? ours, JsonObject? theirs, CancellationToken ct)
    {
        var current = await ProfileAsync(store, name, ct).ConfigureAwait(false);
        // A run cut short right after it wrote the merge base leaves the base in the store: main goes back on top.
        if (current is not null && @base is not null && theirs is not null && JsonNode.DeepEquals(current.Value.Profile, @base) && !JsonNode.DeepEquals(@base, theirs))
        {
            await PutProfileAsync(store, name, theirs, current.Value.Revision, ct).ConfigureAwait(false);
            current = await ProfileAsync(store, name, ct).ConfigureAwait(false);
        }

        // "Theirs" is what the store holds now: main, plus what an earlier VM branch merged into it.
        if (current is null)
            return ours is null ? "Nothing to carry over" : Status(await PutProfileAsync(store, name, ours, null, ct).ConfigureAwait(false));
        long? baseRevision = current.Value.Revision;
        if (@base is null)
        {
            baseRevision = null;
        }
        else if (!JsonNode.DeepEquals(@base, current.Value.Profile))
        {
            baseRevision = Revision(await PutProfileAsync(store, name, @base, current.Value.Revision, ct).ConfigureAwait(false));
            await PutProfileAsync(store, name, current.Value.Profile, baseRevision, ct).ConfigureAwait(false);
        }

        var answer = ours is null
            ? await DeleteProfileAsync(store, name, baseRevision, ct).ConfigureAwait(false)
            : await PutProfileAsync(store, name, ours, baseRevision, ct).ConfigureAwait(false);
        return Status(answer);
    }

    private async Task<(long Revision, JsonObject Profile)?> ProfileAsync(string store, string name, CancellationToken ct)
    {
        try
        {
            var answer = await engines.GetAsync(store, "profiles/" + Uri.EscapeDataString(name), ct).ConfigureAwait(false);
            return answer?["profile"] is JsonObject p && answer["revision"] is JsonValue r ? (r.GetValue<long>(), p) : null;
        }
        catch (AgentFailure failure) when (failure.Status == 404)
        {
            return null;
        }
    }

    private Task<JsonNode?> PutProfileAsync(string store, string name, JsonObject profile, long? baseRevision, CancellationToken ct) =>
        engines.SendAsync(store, HttpMethod.Put, "profiles/" + Uri.EscapeDataString(name),
            new JsonObject { ["profile"] = profile.DeepClone(), ["baseRevision"] = baseRevision }, ct);

    private Task<JsonNode?> DeleteProfileAsync(string store, string name, long? baseRevision, CancellationToken ct) =>
        engines.SendAsync(store, HttpMethod.Delete, "profiles/" + Uri.EscapeDataString(name) + (baseRevision is { } r ? "?baseRevision=" + r : ""), null, ct);

    private static long? Revision(JsonNode? answer) => answer?["revision"] is JsonValue r && r.TryGetValue<long>(out var v) ? v : null;

    private static string Status(JsonNode? answer) => answer?["status"]?.GetValue<string>() switch
    {
        "written" => "Written",
        "conflict" => "Conflict: " + string.Join(", ", (answer?["fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>())),
        "queued" => "Queued",
        var other => other ?? "Written",
    };

    private static JsonObject? Parse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // ---- upstream config repos (9.7) ----

    /// <summary>
    /// The old client's linked config repos and tracked files become <c>upstream</c> records, and each file's stored base
    /// its <c>upstream-applied</c> record at the store's engine, so the shared profiles keep receiving upstream changes
    /// (no note: nothing for the user to do, R8).
    /// </summary>
    private Task UpstreamAsync(LegacyState legacy, string store, CancellationToken ct) => ItemAsync("upstream", "Upstream links", async () =>
    {
        var files = new LegacyConfigRepo(legacy.ConfigRepository).UpstreamFiles();
        if (files.Count == 0) return ("skipped", "None");
        var imported = await new Upstream.UpstreamLinks(agent, engines).ImportLegacyAsync(files, store, ct).ConfigureAwait(false);
        if (imported.Links == 0) return ("skipped", "None");
        return ("done", $"{imported.Tracked} tracked" + (imported.LeftOut > 0 ? $", {imported.LeftOut} left out" : ""));
    });

    // ---- the vault ----

    private async Task SecretsAsync(LegacyState legacy, CancellationToken ct)
    {
        if (!legacy.HasVault) return;
        LegacyVault vault;
        try
        {
            vault = legacy.ReadVault();
        }
        catch (AgentFailure failure)
        {
            Report(new UpgradeItem("vault", "Key vault", "failed", failure.Message));
            return;
        }

        try
        {
            if (vault.Key is { } key && !await agent.VaultKeyIsAsync(key, ct).ConfigureAwait(false))
            {
                // The account was set up before the old vault could be read.
                Report(new UpgradeItem("vault", "Key vault", "failed", "Other vault key; secrets not copied"));
                return;
            }

            if (vault.Leases > 0 || vault.Cleanups > 0)
                Note("Open vault leases end with the upgrade: a VM that needs a secret again asks for it again.");
            Waiting(vault.Secrets.Select(secret => ("secret:" + secret.Name, "Secret " + secret.Name)));
            foreach (var secret in vault.Secrets)
            {
                await ItemAsync("secret:" + secret.Name, "Secret " + secret.Name, async () =>
                {
                    try
                    {
                        await agent.AddSecretAsync(new SecretInput(secret.Name, secret.Description.Trim().Length > 0 ? secret.Description : secret.Name,
                            secret.Value, secret.Username, secret.AutoApprove), ct).ConfigureAwait(false);
                        return ("done", null);
                    }
                    catch (AgentFailure failure) when (failure.Code == "exists")
                    {
                        return ("skipped", "Already there");
                    }
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            if (vault.Key is not null) CryptographicOperations.ZeroMemory(vault.Key);
        }
    }

    // ---- backup keys ----

    /// <summary>The old backup private keys, wrapped under <c>U</c> into a <c>setting</c> record each; their fingerprints.</summary>
    private async Task<IReadOnlyList<string>> BackupKeysAsync(LegacyState legacy, CancellationToken ct)
    {
        var fingerprints = new List<string>();
        foreach (var file in legacy.BackupKeyFiles())
        {
            string? fingerprint = null;
            await ItemAsync("backup-key:" + Path.GetFileName(file), "Old backup key", async () =>
            {
                var (fp, pkcs8) = legacy.ReadBackupKey(file);
                fingerprint = fp;
                try
                {
                    var id = DeviceAgent.LegacyBackupKeyPrefix + fp;
                    if (await agent.RecordExistsAsync(id, ct).ConfigureAwait(false)) return ("skipped", "Already kept");
                    var context = "legacy-backup-key:" + fp;
                    var (epoch, wrapped) = await agent.WrapUnderRootAsync(pkcs8, context, ct).ConfigureAwait(false);
                    await agent.WriteSettingRecordAsync(id, new JsonObject
                    {
                        ["kind"] = "legacy-backup-key", ["fingerprint"] = fp, ["epoch"] = epoch, ["context"] = context, ["wrapped"] = Convert.ToBase64String(wrapped),
                    }, ct).ConfigureAwait(false);
                    // Under the account's root key, so old backups stay restorable.
                    return ("done", "Kept for old backups");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pkcs8);
                }
            }).ConfigureAwait(false);
            // A key copied in an earlier run still has to come off the VMs: its fingerprint is read again.
            if (fingerprint is null)
            {
                try { var (fp, pkcs8) = legacy.ReadBackupKey(file); CryptographicOperations.ZeroMemory(pkcs8); fingerprint = fp; }
                catch (AgentFailure) { }
            }

            if (fingerprint is not null) fingerprints.Add(fingerprint);
        }

        return fingerprints;
    }

    /// <summary>
    /// The account's <c>B.pub</c> enrolled on every VM reached, before any old key comes off (13.2): through the engine
    /// that runs the VM (<c>POST /vms/{name}/backup-recipients</c>); over SSH with the old client's key only for a VM no
    /// engine reaches (a local VM whose adoption failed, a host not added). A VM not reached is retried by a later run.
    /// </summary>
    private async Task RecipientsAsync(LegacyState legacy, IEnumerable<LegacyInstance> instances, string? localEngine, CancellationToken ct)
    {
        if (legacy.BackupKeyFiles().Count == 0) return;
        foreach (var instance in instances)
        {
            await ItemAsync("recipient:" + instance.Name, $"Account backup key on {instance.Name}", async () =>
            {
                var pem = await agent.BackupPublicPemAsync(ct).ConfigureAwait(false);
                if (EngineOf(instance, localEngine) is { } engine)
                {
                    try
                    {
                        await engines.SendAsync(engine, HttpMethod.Put, "device/backup-key", new JsonObject { ["publicKey"] = pem }, ct).ConfigureAwait(false);
                        await engines.PostAsync(engine, $"vms/{Uri.EscapeDataString(instance.Name)}/backup-recipients", null, ct).ConfigureAwait(false);
                        return ("done", "Enrolled");
                    }
                    catch (AgentFailure failure) when (!instance.Local && failure.Status == 404)
                    {
                        // The host runs no VM of this user under that name, or runs the old stack without the route:
                        // never over SSH around its refusal.
                        return ("skipped", await NotHereAsync(engine, ct).ConfigureAwait(false));
                    }
                    catch (AgentFailure)
                    {
                        // The engine does not run this VM yet, or does not reach it: the old client's way.
                    }
                }

                var result = await platform.RunOnVmAsync(Target(legacy, instance), ["construct", "backup", "recipients", "add", "--label", RecipientLabel],
                    Encoding.ASCII.GetBytes(pem), ct).ConfigureAwait(false);
                return result.Exit == 0 ? ("done", "Enrolled") : ("pending", $"Not reached (exit {result.Exit}); retried later");
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After <c>converted</c>: the old public keys come off every VM the account key reached. Through the engine, which
    /// removes one only while the account's key is enrolled there; over SSH only for a VM no engine runs or reaches.
    /// </summary>
    private async Task RemoveLegacyRecipientsAsync(LegacyState legacy, IReadOnlyList<LegacyInstance> instances, IReadOnlyList<string> fingerprints,
        string? localEngine, CancellationToken ct)
    {
        foreach (var instance in instances.Where(i => _done.Contains("recipient:" + i.Name)))
        {
            foreach (var fp in fingerprints)
            {
                await ItemAsync($"unrecipient:{instance.Name}:{fp}", $"Old backup key off {instance.Name}", async () =>
                {
                    if (EngineOf(instance, localEngine) is { } engine)
                    {
                        try
                        {
                            var pem = await agent.BackupPublicPemAsync(ct).ConfigureAwait(false);
                            await engines.SendAsync(engine, HttpMethod.Put, "device/backup-key", new JsonObject { ["publicKey"] = pem }, ct).ConfigureAwait(false);
                            await engines.SendAsync(engine, HttpMethod.Delete, $"vms/{Uri.EscapeDataString(instance.Name)}/backup-recipients/{fp}", null, ct)
                                .ConfigureAwait(false);
                            return ("done", "Removed");
                        }
                        catch (AgentFailure failure) when (failure.Status == 409)
                        {
                            // The engine keeps the old key until the account's key is on the VM.
                            return ("pending", "Retried later");
                        }
                        catch (AgentFailure failure) when (!instance.Local && failure.Status == 404)
                        {
                            return ("skipped", await NotHereAsync(engine, ct).ConfigureAwait(false));
                        }
                        catch (AgentFailure)
                        {
                            // Not run or not reached by the engine: the old client's way.
                        }
                    }

                    var result = await platform.RunOnVmAsync(Target(legacy, instance), ["construct", "backup", "recipients", "remove", fp], null, ct).ConfigureAwait(false);
                    return result.Exit == 0 ? ("done", "Removed") : ("pending", $"Not reached (exit {result.Exit}); retried later");
                }).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Why a shared host answered 404 for a VM's backup recipients: it runs the old Construct, or it does not list the VM as the user's.</summary>
    private async Task<string> NotHereAsync(string engine, CancellationToken ct) =>
        await agent.RunsOldStackAsync(engine, ct).ConfigureAwait(false) == true
            ? "Old Construct, unchanged"
            : "Not yours there; left alone";

    /// <summary>The engine that runs <paramref name="instance"/> at this device: this PC's for a local VM, its host's for a remote one.</summary>
    private string? EngineOf(LegacyInstance instance, string? localEngine) =>
        instance.Local ? localEngine : instance.ServiceUrl is { Length: > 0 } url ? agent.EngineIdFor(url) : null;

    private static LegacyVmTarget Target(LegacyState legacy, LegacyInstance instance) => new(instance.Name, instance.SshHost, instance.SshPort, legacy.KeyFile(instance));

    // ---- local VMs: the switch-over, last ----

    private async Task LocalVmsAsync(LegacyState legacy, IReadOnlyList<LegacyInstance> instances, string? localEngine, double from, CancellationToken ct)
    {
        var local = instances.Where(i => i.Local).ToArray();
        for (var i = 0; i < local.Length; i++)
        {
            var instance = local[i];
            Progress(from + (ReprovisionFrom - from) * i / local.Length);
            await ItemAsync("vm:" + instance.Name, "VM " + instance.Name, async () =>
            {
                if (localEngine is null) return ("failed", "No engine on this PC");
                // No check for the VM at the engine: a failed adoption leaves it registered with its guest not yet
                // switched, and the adoption job resumes the same VM. Only an adoption that succeeded is done.
                var keyFile = legacy.KeyFile(instance);
                if (!File.Exists(keyFile)) return ("failed", "SSH key missing in ~/.ssh");
                var body = new JsonObject
                {
                    ["settings"] = instance.Settings?.DeepClone(), ["instanceState"] = instance.InstanceState?.DeepClone(), ["sshPrivateKey"] = File.ReadAllText(keyFile),
                };
                JsonNode? started;
                try
                {
                    started = await engines.PostAsync(localEngine, $"vms/{instance.Name}/adopt", body, ct).ConfigureAwait(false);
                }
                finally
                {
                    body["sshPrivateKey"] = null;
                }

                var job = started?["jobId"]?.GetValue<string>() ?? throw new AgentFailure(502, "adopt-failed", "The engine started no adoption job.");
                var (state, error) = await WaitForJobAsync(localEngine, job, AdoptTimeout, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
                if (instance.Settings?["micPassthrough"] is JsonValue mic && mic.TryGetValue<bool>(out var on) && on)
                    settings.Update(s => s.Mic.Contains($"{localEngine}/{instance.Name}") ? s : s with { Mic = [.. s.Mic, $"{localEngine}/{instance.Name}"] });
                // It runs (a VM that was off was started); the engine's reason says why one failed.
                return state == "succeeded" ? ("done", "Switched over") : ("failed", $"Adoption {state}{(error is { Length: > 0 } ? ": " + error : "; see its log")}");
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The old client's autoinstall images (<c>&lt;vm&gt;-autoinstall.iso</c>, about 3 GB each) once the move is complete
    /// (F33): the engine builds its own media, and its ISO builder refuses an already patched image as its source. Only
    /// in the old client's own install folders; a copy anywhere else stays, and so does one Windows will not let go of.
    /// </summary>
    private static void DeleteInstallImages(LegacyState legacy)
    {
        // An install named with --from elsewhere (an extracted copy in Downloads) is the user's, and so are its images.
        foreach (var folder in legacy.OwnInstallFolders())
        {
            foreach (var image in Directory.EnumerateFiles(folder, "*-autoinstall.iso", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(image); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // ---- last: the adopted VMs on the new version (owner, 2026-11-21: one click) ----

    /// <summary>
    /// Each VM adopted here is reprovisioned with the engine's guest payload, so Move over leaves it on the new version:
    /// it keeps running and its services restart once; the Move over click is the confirmation. The job goes through
    /// the agent's job route, which adds this device's clone credentials. A failure is this row's alone: Move over
    /// again retries it, as Reprovision on the VM's page would.
    /// </summary>
    /// <summary>Where the reprovisions start on the bar: everything before them is quick.</summary>
    private const double ReprovisionFrom = 20;

    private const double ReprovisionTo = 98;

    private async Task ReprovisionAsync(IReadOnlyList<LegacyInstance> instances, string? localEngine, CancellationToken ct)
    {
        if (localEngine is null) return;
        var local = instances.Where(i => i.Local).ToArray();
        for (var i = 0; i < local.Length; i++)
        {
            var instance = local[i];
            var at = i;
            if (!_done.Contains("vm:" + instance.Name)) continue;
            var id = "reprovision:" + instance.Name;
            var label = $"VM {instance.Name} on the new version";
            Progress(ReprovisionFrom + (ReprovisionTo - ReprovisionFrom) * at / local.Length);
            await ItemAsync(id, label, async () =>
            {
                // The row shows the job's own percentage and step (R7); the bar moves with it.
                var problem = await ReprovisionVmAsync(localEngine, instance.Name, (percent, step) =>
                {
                    Progress(ReprovisionFrom + (ReprovisionTo - ReprovisionFrom) * (at + percent / 100.0) / local.Length);
                    Report(new UpgradeItem(id, label, "running", step, percent));
                }, ct).ConfigureAwait(false);
                // The VM kept running; Move over again retries a failure, as Reprovision on the VM's page would.
                return problem is null ? ("done", "On the new version") : ("failed", problem);
            }, running: "Starting").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reprovisions <paramref name="vm"/> at <paramref name="engine"/> through the agent's job route (which adds this device's
    /// clone credentials) and waits for it: null when it succeeded, else why not. <paramref name="running"/> sees the job's
    /// percentage and step as they change. A job that already runs on the VM (one started before its key arrived) is waited
    /// for first, then this one runs.
    /// </summary>
    private async Task<string?> ReprovisionVmAsync(string engine, string vm, Action<int, string> running, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var answer = await agent.StartVmJobAsync(engine, vm,
                new JsonObject { ["kind"] = "provision", ["requestId"] = Guid.NewGuid().ToString("D") }, ct).ConfigureAwait(false);
            var body = Parse(answer.Body);
            if (answer.Status == 409 && attempt == 0 && Text(body, "code") == "operation-in-progress" && Text(body, "jobId") is { Length: > 0 } other)
            {
                running(0, "Waiting for the VM's running job");
                await WaitForJobAsync(engine, other, ReprovisionTimeout, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            if (answer.Status is < 200 or >= 300) return "Not started: " + (Text(body, "title") ?? $"the engine answered {answer.Status}");
            var job = Text(body, "jobId") ?? throw new AgentFailure(502, "provision-failed", "The engine started no reprovision job.");
            (int, string)? shown = null;
            var (state, error) = await WaitForJobAsync(engine, job, ReprovisionTimeout, TimeSpan.FromSeconds(2), ct, read =>
            {
                if (read["overall"] is not JsonObject overall || overall["percent"] is not JsonValue p || !p.TryGetValue<int>(out var percent)) return;
                var step = overall["label"] is JsonValue l && l.TryGetValue<string>(out var text) && text.Length > 0 ? text : "Reprovisioning";
                percent = Math.Clamp(percent, 0, 100);
                if (shown == (percent, step)) return;
                shown = (percent, step);
                running(percent, step);
            }).ConfigureAwait(false);
            return state == "succeeded" ? null : $"Reprovision {state}{(error is { Length: > 0 } ? ": " + error : "")}";
        }
    }

    /// <summary>
    /// Polls a job until it ends or <paramref name="limit"/> passes, at most every <paramref name="poll"/>; its end state and error.
    /// <paramref name="read"/> sees each answer while it runs.
    /// </summary>
    private async Task<(string State, string? Error)> WaitForJobAsync(string engine, string job, TimeSpan limit, TimeSpan poll, CancellationToken ct,
        Action<JsonNode>? read = null)
    {
        var until = DateTimeOffset.UtcNow + limit;
        var wait = TimeSpan.FromMilliseconds(Math.Min(250, poll.TotalMilliseconds));
        while (true)
        {
            var answer = await engines.GetAsync(engine, "jobs/" + job, ct).ConfigureAwait(false);
            var state = answer?["state"]?.GetValue<string>() ?? "unknown";
            if (state is not ("queued" or "running")) return (state, answer?["error"] is JsonValue e && e.TryGetValue<string>(out var text) ? text : null);
            if (answer is not null) read?.Invoke(answer);
            if (DateTimeOffset.UtcNow > until) return ("still running", null);
            await Task.Delay(wait, ct).ConfigureAwait(false);
            wait = TimeSpan.FromMilliseconds(Math.Min(wait.TotalMilliseconds * 2, poll.TotalMilliseconds));
        }
    }
}
