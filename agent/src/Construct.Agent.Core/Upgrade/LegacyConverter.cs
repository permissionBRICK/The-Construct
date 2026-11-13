using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;

namespace Construct.Agent.Upgrade;

/// <summary>
/// The converter (13.1): it reads the old client's state (<see cref="LegacyState"/>) and copies it into the
/// new stack, writing only to the engines, the store and the agent's own files (<c>bridge.json</c>,
/// <c>device.json</c>, <c>legacy-upstream.json</c>). It changes nothing of the old files. It can be run
/// again at any point: an item listed as done in <c>bridge.json</c>, or already present where it goes, is
/// left alone. Its last step switches the local VMs over to the engine (the adopt job); then
/// <c>{stage: converted}</c> is written, and only after that the old backup key comes off the VMs.
/// </summary>
public sealed class LegacyConverter(DeviceAgent agent, IDeviceEngines engines, DeviceSettings settings, BridgeFile bridge,
    IUpgradePlatform platform, string dataDirectory, Action<UpgradeItem> progress)
{
    public const string RecipientLabel = "construct-account";

    /// <summary>Called before each item with its id; tests throw here to cut a run short.</summary>
    public Func<string, Task>? BeforeItem { get; init; }

    /// <summary>How long one adoption job may take.</summary>
    public TimeSpan AdoptTimeout { get; init; } = TimeSpan.FromMinutes(15);

    private readonly List<UpgradeItem> _items = [];
    private readonly List<string> _notes = [];
    private HashSet<string> _done = [];

    /// <summary>Runs every item; true when the conversion is complete (<c>converted</c>).</summary>
    public async Task<bool> RunAsync(LegacyState legacy, CancellationToken ct)
    {
        if (!agent.IsRegistered) throw new AgentFailure(409, "not-registered", "Set up the recovery code first: the converted records are sealed under the account's keys.");
        var start = bridge.Read();
        _done = start.Done.ToHashSet(StringComparer.Ordinal);
        var store = start.StoreEngine ?? agent.StoreEngineId ?? throw new AgentFailure(409, "no-store", "This device has no store yet.");
        bridge.Update(b => b with { Stage = b.Stage == BridgeState.Converted ? b.Stage : "converting", From = legacy.InstallFolder, Items = [] });

        var instances = legacy.Instances();
        await ChannelAsync(legacy, ct).ConfigureAwait(false);
        await HostsAsync(legacy, ct).ConfigureAwait(false);
        await ProfilesAsync(legacy, store, ct).ConfigureAwait(false);
        await UpstreamAsync(legacy, ct).ConfigureAwait(false);
        await SecretsAsync(legacy, ct).ConfigureAwait(false);
        var legacyKeys = await BackupKeysAsync(legacy, ct).ConfigureAwait(false);
        await RecipientsAsync(legacy, instances, start.LocalEngine, ct).ConfigureAwait(false);
        await LocalVmsAsync(legacy, instances, start.LocalEngine, ct).ConfigureAwait(false);

        var complete = _items.All(i => i.State is not "failed");
        if (complete && bridge.Read().Stage != BridgeState.Converted)
            bridge.Update(b => b with { Stage = BridgeState.Converted, ConvertedAt = DateTimeOffset.UtcNow });
        if (bridge.Read().Stage == BridgeState.Converted) await RemoveLegacyRecipientsAsync(legacy, instances, legacyKeys, start.LocalEngine, ct).ConfigureAwait(false);
        bridge.Update(b => b with { Items = _items.ToArray(), Notes = _notes.Distinct().ToArray() });
        return complete;
    }

    // ---- one item ----

    private async Task ItemAsync(string id, string label, Func<Task<(string State, string? Message)>> work, bool remember = true)
    {
        if (_done.Contains(id))
        {
            Report(new UpgradeItem(id, label, "skipped", "Copied in an earlier run."));
            return;
        }

        Report(new UpgradeItem(id, label, "running"));
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

        if (remember && item.State is "done" or "skipped")
        {
            _done.Add(id);
            bridge.Update(b => b with { Done = b.Done.Contains(id) ? b.Done : [.. b.Done, id] });
        }

        Report(item);
    }

    private void Report(UpgradeItem item)
    {
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
        if (current != "stable" || current == channel) return Task.FromResult<(string, string?)>(("done", $"The channel stays {current}."));
        settings.Update(s => s with { Channel = channel });
        return Task.FromResult<(string, string?)>(("done", $"The channel is {channel}."));
    });

    // ---- remote hosts ----

    private async Task HostsAsync(LegacyState legacy, CancellationToken ct)
    {
        foreach (var host in legacy.Hosts())
        {
            await ItemAsync("host:" + host.Slug, "Host " + new Uri(host.Url).Host, async () =>
            {
                if (host.Pin is null) return ("failed", "The old client has no pinned certificate for this host; add it by hand in Settings.");
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

                if (known.Enrolled) return ("done", host.VaultMode is null ? "Added and enrolled." : $"Added and enrolled; vault mode {host.VaultMode}.");
                // Carried over all the same: the host keeps its pin and the old sign-in, and connecting it again from
                // Settings enrols this device once the host can. A second run leaves it alone.
                return ("done", known.SignedIn
                    ? $"Added with your old sign-in; this device is not enrolled there yet ({problem ?? "the host refused"}). Connect it again from Settings later."
                    : "Added without a sign-in (a Windows sign-in host has no token to carry over): sign in there in Settings.");
            }).ConfigureAwait(false);
        }
    }

    // ---- profiles (9.4) ----

    private async Task ProfilesAsync(LegacyState legacy, string store, CancellationToken ct)
    {
        var repo = new LegacyConfigRepo(legacy.ConfigRepository);
        if (!repo.Exists) return;
        foreach (var (name, text) in repo.Profiles())
        {
            await ItemAsync("profile:" + name, "Profile " + name, async () =>
            {
                if (Parse(text) is not { } profile) return ("skipped", "Not a JSON object; left in the old folder.");
                if (await ProfileAsync(store, name, ct).ConfigureAwait(false) is not null) return ("skipped", "Already in the store.");
                try { return ("done", Status(await PutProfileAsync(store, name, profile, null, ct).ConfigureAwait(false))); }
                catch (AgentFailure failure) when (failure.Status == 400) { return ("skipped", "The engine refused it as no valid profile; left in the old folder."); }
            }).ConfigureAwait(false);
        }

        if (!repo.IsGit) _notes.Add("The config folder is no git repository (or git is missing): VM branches were not looked at.");
        foreach (var change in repo.UnmergedChanges(legacy.Instances().Select(i => i.ConfigBranch)))
        {
            await ItemAsync($"branch:{change.Branch}:{change.Profile}", $"Unmerged change to {change.Profile} on {change.Branch}", async () =>
            {
                var ours = change.Ours is null ? null : Parse(change.Ours);
                var @base = change.Base is null ? null : Parse(change.Base);
                var theirs = change.Theirs is null ? null : Parse(change.Theirs);
                if (change.Ours is not null && ours is null || change.Base is not null && @base is null || change.Theirs is not null && theirs is null)
                    return ("skipped", "A side is not a JSON object; left in the old folder.");
                try { return ("done", await BranchChangeAsync(store, change.Profile, @base, ours, theirs, ct).ConfigureAwait(false)); }
                catch (AgentFailure failure) when (failure.Status == 400) { return ("skipped", "The engine refused a side as no valid profile; left in the old folder."); }
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
            return ours is null ? "Nothing to carry over." : Status(await PutProfileAsync(store, name, ours, null, ct).ConfigureAwait(false));
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
        "written" => "Written.",
        "conflict" => "A conflict was opened: " + string.Join(", ", (answer?["fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>())) + ".",
        "queued" => "Queued at the engine.",
        var other => other ?? "Written.",
    };

    private static JsonObject? Parse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // ---- upstream links: kept as data ----

    private Task UpstreamAsync(LegacyState legacy, CancellationToken ct) => ItemAsync("upstream", "Company upstream links", () =>
    {
        var files = new LegacyConfigRepo(legacy.ConfigRepository).UpstreamFiles();
        if (files.Count == 0) return Task.FromResult<(string, string?)>(("skipped", "No upstream links."));
        var document = new JsonObject { ["v"] = 1, ["files"] = new JsonObject(files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.Value))) };
        PrivateFiles.Write(Path.Combine(dataDirectory, "legacy-upstream.json"), Encoding.UTF8.GetBytes(document.ToJsonString()));
        _notes.Add("Company upstream links were kept as data in legacy-upstream.json; the new stack does not use them yet.");
        return Task.FromResult<(string, string?)>(("done", $"{files.Count} file(s) kept for when upstream links come to the new stack."));
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
                Report(new UpgradeItem("vault", "Key vault", "failed",
                    "The account's vault key is not the old one (the account was set up before the old vault could be read); the secrets were not copied."));
                return;
            }

            if (vault.Leases > 0 || vault.Cleanups > 0)
                _notes.Add("Open vault leases end with the upgrade: a VM that needs a secret again asks for it again.");
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
                        return ("skipped", "A secret of this name is in the vault already.");
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
                    if (await agent.RecordExistsAsync(id, ct).ConfigureAwait(false)) return ("skipped", "Kept already.");
                    var context = "legacy-backup-key:" + fp;
                    var (epoch, wrapped) = await agent.WrapUnderRootAsync(pkcs8, context, ct).ConfigureAwait(false);
                    await agent.WriteSettingRecordAsync(id, new JsonObject
                    {
                        ["kind"] = "legacy-backup-key", ["fingerprint"] = fp, ["epoch"] = epoch, ["context"] = context, ["wrapped"] = Convert.ToBase64String(wrapped),
                    }, ct).ConfigureAwait(false);
                    return ("done", "Kept under the account's root key, so old backups stay restorable.");
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
    /// engine reaches yet (a local VM before its adoption, a host not added). A VM not reached is retried by a later run.
    /// </summary>
    private async Task RecipientsAsync(LegacyState legacy, IReadOnlyList<LegacyInstance> instances, string? localEngine, CancellationToken ct)
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
                        return ("done", "Enrolled through the engine.");
                    }
                    catch (AgentFailure failure) when (!instance.Local && failure.Status == 404)
                    {
                        // The host runs no VM of this user under that name: never over SSH around its refusal.
                        return ("skipped", "The host does not list this VM as yours; its backup recipients are left alone.");
                    }
                    catch (AgentFailure)
                    {
                        // The engine does not run this VM yet, or does not reach it: the old client's way.
                    }
                }

                var result = await platform.RunOnVmAsync(Target(legacy, instance), ["construct", "backup", "recipients", "add", "--label", RecipientLabel],
                    Encoding.ASCII.GetBytes(pem), ct).ConfigureAwait(false);
                return result.Exit == 0 ? ("done", "Enrolled.") : ("pending", $"The VM was not reached or refused (exit {result.Exit}); a later run retries.");
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
                            return ("done", "Removed through the engine.");
                        }
                        catch (AgentFailure failure) when (failure.Status == 409)
                        {
                            return ("pending", "The engine keeps the old key until the account's key is on the VM; a later run retries.");
                        }
                        catch (AgentFailure failure) when (!instance.Local && failure.Status == 404)
                        {
                            return ("skipped", "The host does not list this VM as yours; its backup recipients are left alone.");
                        }
                        catch (AgentFailure)
                        {
                            // Not run or not reached by the engine: the old client's way.
                        }
                    }

                    var result = await platform.RunOnVmAsync(Target(legacy, instance), ["construct", "backup", "recipients", "remove", fp], null, ct).ConfigureAwait(false);
                    return result.Exit == 0 ? ("done", "Removed.") : ("pending", $"The VM was not reached (exit {result.Exit}); a later run retries.");
                }).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The engine that runs <paramref name="instance"/> at this device: this PC's for a local VM, its host's for a remote one.</summary>
    private string? EngineOf(LegacyInstance instance, string? localEngine) =>
        instance.Local ? localEngine : instance.ServiceUrl is { Length: > 0 } url ? agent.EngineIdFor(url) : null;

    private static LegacyVmTarget Target(LegacyState legacy, LegacyInstance instance) => new(instance.Name, instance.SshHost, instance.SshPort, legacy.KeyFile(instance));

    // ---- local VMs: the switch-over, last ----

    private async Task LocalVmsAsync(LegacyState legacy, IReadOnlyList<LegacyInstance> instances, string? localEngine, CancellationToken ct)
    {
        foreach (var instance in instances.Where(i => i.Local))
        {
            await ItemAsync("vm:" + instance.Name, "VM " + instance.Name, async () =>
            {
                if (localEngine is null) return ("failed", "No engine on this PC: install it first (the dialog's first step).");
                // No check for the VM at the engine: a failed adoption leaves it registered with its guest not yet
                // switched, and the adoption job resumes the same VM. Only an adoption that succeeded is done.
                var keyFile = legacy.KeyFile(instance);
                if (!File.Exists(keyFile)) return ("failed", "The old client's SSH key for this VM is missing (~/.ssh).");
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
                var state = await WaitForJobAsync(localEngine, job, ct).ConfigureAwait(false);
                if (instance.Settings?["micPassthrough"] is JsonValue mic && mic.TryGetValue<bool>(out var on) && on)
                    settings.Update(s => s.Mic.Contains($"{localEngine}/{instance.Name}") ? s : s with { Mic = [.. s.Mic, $"{localEngine}/{instance.Name}"] });
                return state == "succeeded" ? ("done", "Switched over to the engine; it kept running.") : ("failed", $"The adoption job ended {state}; see the job's log, then retry.");
            }).ConfigureAwait(false);
        }
    }

    private async Task<string> WaitForJobAsync(string engine, string job, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + AdoptTimeout;
        while (true)
        {
            var state = (await engines.GetAsync(engine, "jobs/" + job, ct).ConfigureAwait(false))?["state"]?.GetValue<string>() ?? "unknown";
            if (state is not ("queued" or "running")) return state;
            if (DateTimeOffset.UtcNow > until) return "still running";
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
    }
}
