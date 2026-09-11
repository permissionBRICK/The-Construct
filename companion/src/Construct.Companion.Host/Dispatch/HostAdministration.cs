using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using HostIdentity = Construct.Companion.Core.Remote.RemoteHost;
using HostRecord = Construct.Companion.Core.Ipc.RemoteHost;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Host.Dispatch;

// The port of hostadmin.js + hostadmin-ui.js: enrolment, per-host admin models driven by
// hostadmin.* messages, the host extras of an instance (children, offer, idle policy) and polling.
public sealed partial class HostAdministration(IStateFileSystem files, ITokenStore tokens, IRemoteApi api,
    IPrompts prompts, ILauncher launcher, IClock clock, IpcSettings settings, IpcEvents events, RuntimeMessageBus bus, CompanionInstances instances, RemoteVmWizard vmWizard)
{
    private readonly ConcurrentDictionary<string, JsonArray> childCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim enrollment = new(1, 1);
    private readonly ConcurrentDictionary<string, Model> models = new(StringComparer.Ordinal);

    // Enrolled hosts (hosts.json) plus every host a registered remote instance points at.
    public IReadOnlyList<HostRecord> List()
    {
        var hosts = Enrolled.OfType<JsonObject>().ToDictionary(h => HostIdentity.HostSlug(Text(h["url"])), h => h);
        foreach (var definition in instances.Registry.List())
            if (definition["service"] is JsonObject service && Text(service["url"]).Length > 0) hosts.TryAdd(HostIdentity.HostSlug(Text(service["url"])), service);
        return hosts.Select(h => new HostRecord(h.Key, HostIdentity.NormalizeServiceUrl(Text(h.Value["url"])), Text(h.Value["auth"]) == "token" ? "token" : "negotiate", HostIdentity.ReadPin(files, Text(h.Value["url"])).Length > 0, models.TryGetValue(h.Key, out var model) ? Text(model.View["mode"]) == "admin" : null)).ToArray();
    }
    public async Task CreateFirstVmAsync(string? preferred, CancellationToken ct)
    {
        var available = List();
        if (available.Count == 0)
        {
            if (await prompts.ConfirmAsync(new ConfirmationPrompt("No Construct remote host is configured yet.", "", "Add a remote host"), ct))
            {
                var url = await prompts.InputAsync(new("Add remote host", "Construct host URL"), ct); if (string.IsNullOrWhiteSpace(url)) return;
                var token = await prompts.InputAsync(new("Add remote host", "API token (leave empty for Windows sign-in)", Password: true), ct); if (token is null) return;
                await AddAsync(new(url, token.Length == 0 ? null : token), ct);
            }
            return;
        }
        var host = preferred is null ? available[0] : available.FirstOrDefault(h => h.Slug == preferred) ?? throw new IpcFailure(404, "hostNotFound", "Unknown host.");
        if (preferred is null && available.Count > 1)
        {
            var choice = await prompts.PickAsync(new("Create a VM on which host?", available.Select(h => new PickItem(h.Slug, new Uri(h.Url).Host, h.Url)).ToArray()), ct);
            if (choice?.FirstOrDefault() is not {} slug) return; host = available.Single(h => h.Slug == slug);
        }
        await vmWizard.RunAsync(host, ct);
    }
    public JsonObject Snapshot(string slug) => new() { ["type"] = "hostadmin.state", ["state"] = Get(slug).View.DeepClone() };
    public async Task<HostRecord> AddAsync(AddRemoteHost input, CancellationToken ct)
    {
        await enrollment.WaitAsync(ct);
        try
        {
            string url;
            try { url = HostIdentity.NormalizeServiceUrl(input.Url); HostIdentity.AssertTransportSafe(url); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new IpcFailure(400, "invalidHost", "A secure host URL is required."); }
            var slug = HostIdentity.HostSlug(url); var pin = HostIdentity.FormatFingerprint(input.Fingerprint);
            if (pin.Length == 0) pin = HostIdentity.ReadPin(files, url);
            var secure = new Uri(url).Scheme == "https";
            string observed = "";
            var response = await api.SendAsync(new("GET", new Uri(url + "/api/v1/health"), actual => { observed = HostIdentity.FormatFingerprint(actual); return !secure || pin.Length == 0 || HostIdentity.FingerprintsMatch(pin, actual); }), ct);
            if (response.StatusCode is not (200 or 503)) throw new IpcFailure(502, "hostUnavailable", "The host health check failed.");
            if (secure && (observed.Length == 0 || pin.Length > 0 && !HostIdentity.FingerprintsMatch(pin, observed))) throw new IpcFailure(409, "pinMismatch", "The host certificate did not match the expected pin.");
            if (secure && pin.Length == 0 && !await prompts.ConfirmAsync("Trust Construct host certificate", $"{url}\nSHA-256: {observed}\nConfirm this fingerprint against the host before continuing.", ct)) throw new IpcFailure(409, "enrollmentCancelled", "Host enrollment was cancelled.");
            pin = observed.Length > 0 ? observed : pin;
            var authentication = input.Token is { Length: > 0 } ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate;
            var who = await api.SendAsync(new("GET", new Uri(url + "/api/v1/whoami"), actual => !secure || HostIdentity.FingerprintsMatch(pin, actual), Authentication: authentication, Token: input.Token is null ? null : new Secret(input.Token)), ct);
            if (who.StatusCode != 200 || who.Body is not { } body || body.ValueKind != JsonValueKind.Object || body.TryGetProperty("known", out var known) && known.ValueKind == JsonValueKind.False || body.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
                throw new IpcFailure(403, "enrollmentRefused", "This identity is not enrolled or enabled on the host.");
            if (secure) HostIdentity.WritePin(files, url, pin);
            if (input.Token is { Length: > 0 }) await tokens.WriteAsync(slug, new(input.Token), ct); else await tokens.DeleteAsync(slug, ct);
            var hosts = Enrolled; var existing = hosts.OfType<JsonObject>().FirstOrDefault(h => HostIdentity.SameServiceUrl(Text(h["url"]), url)); if (existing is not null) hosts.Remove(existing);
            hosts.Add(new JsonObject { ["url"] = url, ["auth"] = authentication == RemoteAuthentication.Token ? "token" : "negotiate" });
            files.CreateDirectory(settings.Directory); files.WriteFileAtomic(HostsPath, StateJson.Bytes(new JsonObject() { ["v"] = 1, ["hosts"] = hosts })); if (models.TryGetValue(slug, out var model)) model.Host = Find(slug);
            events.Companion(new { type = "instances" }); return Find(slug);
        }
        finally { enrollment.Release(); }
    }
    public async Task RemoveAsync(string slug, CancellationToken ct)
    {
        await enrollment.WaitAsync(ct);
        try
        {
            var host = Find(slug);
            if (instances.Registry.List().Any(i => HostIdentity.SameServiceUrl(Text(i["service"]?["url"]), host.Url))) throw new IpcFailure(409, "hostInUse", "Remove this host's registered instances before removing the host.");
            var remaining = new JsonArray(Enrolled.OfType<JsonObject>().Where(h => !HostIdentity.SameServiceUrl(Text(h["url"]), host.Url)).Select(h => h.DeepClone()).ToArray());
            files.WriteFileAtomic(HostsPath, StateJson.Bytes(new JsonObject() { ["v"] = 1, ["hosts"] = remaining }));
            await tokens.DeleteAsync(slug, ct); var pin = HostIdentity.PinPath(files, host.Url); if (pin is not null) files.DeleteFile(pin); models.TryRemove(slug, out _); events.Companion(new { type = "instances" });
        }
        finally { enrollment.Release(); }
    }
    // Synchronous 400s for the HTTP route; Action() repeats the checks whose values it uses.
    public void Validate(string slug, JsonObject message)
    {
        Find(slug); var type = Text(message["type"]);
        if (type.Length == 0) throw new IpcFailure(400, "invalidMessage", "A message type is required.");
        if (type == "hostadmin.tab") RequireTab(Text(message["tab"]));
        if (type != "hostadmin.action") return;
        var action = Text(message["action"]); var args = message["args"] as JsonObject ?? [];
        if (action.Length == 0) throw new IpcFailure(400, "invalidMessage", "An action is required.");
        var form = action switch { "createUser" => "newUser", "updateUser" => "user", "saveAllowance" => "allowance", "saveOverrides" => "overrides", _ => null };
        if (form is not null) RequireForm(form, args);
        if (action == "shareVm") RequireScope(args);
        if (action == "updatesResolve") RequireResolve(args);
        if (action == "saveConfig") RequireSections(args);
    }
    public async Task DispatchAsync(string slug, JsonObject message, CancellationToken ct)
    {
        var model = Get(slug); await model.Serial.WaitAsync(ct);
        try
        {
            // A window closed while this dispatch ran must not re-arm polling (Close bumps Visibility).
            var visibility = Volatile.Read(ref model.Visibility);
            var type = Text(message["type"]);
            if (type == "hostadmin.signIn")
            {
                var method = await prompts.PickAsync(new("Sign in to Construct host", [new("negotiate", "Windows identity"), new("token", "API token")]), ct);
                if (method is null) return;
                var token = method.Contains("token") ? await prompts.InputAsync(new("Sign in to Construct host", "API token (kept in the per-user protected token store)", Password: true), ct) : null;
                if (method.Contains("token") && token is null) return;
                await AddAsync(new(model.Host.Url, token), ct);
                model.Host = Find(slug);
            }
            var client = Client(model.Host);
            switch (type)
            {
                case "hostadmin.ready": case "hostadmin.refresh": case "hostadmin.signIn": await Detect(model, client, ct); await Load(model, client, ct); await AdvanceUpdate(model, client, ct); break;
                case "hostadmin.tab": model.State["activeTab"] = RequireTab(Text(message["tab"])); await Load(model, client, ct); break;
                case "hostadmin.action": await Action(model, client, Text(message["action"]), message["args"] as JsonObject ?? [], ct); break;
                default: Notice(model, "This host-administration message is unsupported."); break;
            }
            if (visibility == Volatile.Read(ref model.Visibility)) model.Opened = true;
        }
        catch (RemoteApiException e) { Refusal(model, e); }
        catch (IpcFailure e) { Notice(model, e.Message); throw; }
        finally { Publish(model); model.Serial.Release(); }
    }
    // The instance panel's host-backed extras; detection is refreshed at most once a minute.
    public async Task RefreshExtrasAsync(CompanionInstance entry, CancellationToken ct)
    {
        JsonNode? children = null, offer = null, idle = null;
        if (instances.Remote(entry) is { } client)
        {
            try
            {
                var slug = HostIdentity.HostSlug(client.BaseUrl); var model = Get(slug);
                await model.Serial.WaitAsync(ct);
                try { if (clock.UtcNow - model.LastDetection >= TimeSpan.FromSeconds(60)) { await Detect(model, client, ct); model.View = model.State.DeepClone().AsObject(); model.LastDetection = clock.UtcNow; } }
                finally { model.Serial.Release(); }
                if (Text(model.View["mode"]) == "admin") offer = new JsonObject { ["url"] = client.BaseUrl, ["host"] = client.Host };
                if (StateJson.Boolean(model.View["features"]?["children"]) == true)
                {
                    var input = new JsonObject { ["backend"] = "hyperv-remote", ["supported"] = true, ["primary"] = entry.Name };
                    try { var rows = await Construct.Companion.Core.Drivers.VmPower.QueryChildrenAsync(client, entry.Name, ct); childCache[entry.Name] = rows; input["items"] = rows.DeepClone(); }
                    catch (RemoteApiException e)
                    {
                        if (childCache.TryGetValue(entry.Name, out var previous)) input["items"] = previous.DeepClone();
                        input["problem"] = e.Message;
                    }
                    children = HostAdminViews.ChildrenCard(input, clock.UtcNow);
                }
                bus.Publish(entry.Name, new { type = "children", instance = entry.Name, children });
                idle = IdlePolicy(await client.RequestAsync("GET", "/vms/" + HostIdentity.Encode(entry.Name) + "/idle-policy", cancellationToken: ct));
            }
            catch (RemoteApiException) { } // extras are optional: an unreachable host leaves them null, as the extension does
        }
        if (instances.Remote(entry) is null) { childCache.TryRemove(entry.Name, out _); bus.Publish(entry.Name, new { type = "children", instance = entry.Name, children }); }
        bus.Publish(entry.Name, new { type = "hostAdminOffer", instance = entry.Name, offer });
        bus.Publish(entry.Name, new { type = "idlePolicy", instance = entry.Name, idlePolicy = idle });
    }
    public async Task ChildActionAsync(CompanionInstance entry, string action, string child, CancellationToken ct)
    {
        var snapshot = bus.Snapshot(entry.Name);
        var rows = snapshot.TryGetValue("children", out var cached) ? JsonNode.Parse(cached.GetRawText())?["children"]?["items"] as JsonArray : null;
        var row = rows?.OfType<JsonObject>().FirstOrDefault(r => Text(r["name"]) == child) ?? throw new IpcFailure(400, "unknownChild", "The child was not listed for this instance.");
        if (action == "childConsole")
        {
            if (StateJson.Boolean(row["canConsole"]) != true)
                events.Message(entry.Name, new { type = "lifecyclePrepared", id = action, error = $"Console access is unavailable for \"{child}\". Refresh and check that it is running and you have console access." });
            else
            {
                try { await GuestConsole.OpenAsync(entry.Ssh, launcher, child, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (InvalidOperationException e) { events.Message(entry.Name, new { type = "lifecyclePrepared", id = action, error = $"Could not open the console for \"{child}\": {e.Message}" }); }
                catch { events.Message(entry.Name, new { type = "lifecyclePrepared", id = action, error = $"Could not open the console for \"{child}\": Check that the primary VM and Construct client are connected and retry." }); }
            }
            return;
        }
        var client = instances.Remote(entry) ?? throw new IpcFailure(409, "localInstance", "This instance has no host service.");
        if (action == "childDelete")
        { var confirmation = ChildDeleteConfirmation(row); if (await prompts.ConfirmAsync(Text(confirmation["title"]), Text(confirmation["detail"]), ct)) await client.DeleteVmAsync(child, null, ct); }
        else if (await prompts.ConfirmAsync($"Request a graceful shutdown of \"{child}\"?", "The guest is asked to shut down. The service never forces it off.", ct))
        {
            var job = await client.LifecycleAsync(child, new JsonObject { ["action"] = "shutdown" }, ct); var id = Text(job?["jobId"]);
            if (id.Length > 0) for (var i = 0; i < 60; i++)
            { var outcome = await client.GetJobAsync(id, ct); var status = Text(outcome?["state"]); if (status is "succeeded" or "failed" or "cancelled") { if (status != "succeeded") events.Message(entry.Name, new { type = "lifecyclePrepared", id = action, error = "The child shutdown did not succeed." }); break; } await clock.DelayAsync(TimeSpan.FromSeconds(2), ct); }
        }
        await RefreshExtrasAsync(entry, ct);
    }
    public void Close(string slug) { if (models.TryGetValue(slug, out var model)) { Interlocked.Increment(ref model.Visibility); model.Opened = false; } }
    public async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await clock.DelayAsync(TimeSpan.FromSeconds(5), ct);
                foreach (var m in models.Values.Where(m => m.Opened))
                    if (PollInterval(m.View) is { } interval && clock.UtcNow - m.LastPoll >= TimeSpan.FromMilliseconds(interval))
                    { m.LastPoll = clock.UtcNow; await DispatchAsync(m.Host.Slug, new() { ["type"] = "hostadmin.refresh" }, ct); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task Detect(Model m, RemoteHostClient client, CancellationToken ct)
    {
        m.State["busy"] = true;
        try
        {
            var input = new JsonObject { ["backend"] = "hyperv-remote", ["host"] = client.Host };
            static JsonObject Error(RemoteApiException e) => new() { ["status"] = e.Status, ["code"] = e.Code, ["message"] = "Host request failed.", ["body"] = e.Body?.DeepClone() };
            try { input["health"] = await client.HealthAsync(ct); } catch (RemoteApiException e) { input["healthError"] = Error(e); }
            if (input["healthError"] is null || IsMaintenance(input["healthError"]))
                try { input["whoami"] = await client.WhoamiAsync(ct); } catch (RemoteApiException e) { input["whoamiError"] = Error(e); }
            var resolved = Classify(input);
            foreach (var (key, value) in resolved) m.State[key] = value?.DeepClone();
            m.State["tabs"] = TabsFor(m.State["features"]!.AsObject());
            if (Text(m.State["mode"]) is "admin" or "user") m.State["lastKnownAt"] = clock.UtcNow.ToString("O");
        }
        finally { m.State["busy"] = false; }
    }
    private async Task Load(Model m, RemoteHostClient client, CancellationToken ct)
    {
        if (Text(m.State["mode"]) != "admin") return;
        var tab = Text(m.State["activeTab"]); if (tab == "maintenance" && StateJson.Boolean(m.State["features"]?["updates"]) != true) return;
        m.State["busy"] = true;
        try
        {
            switch (tab)
            {
                case "overview": m.State["overview"] = HostAdminViews.Overview(await client.HostStatusAsync(ct)); break;
                case "vms": m.State["vms"] = new JsonObject { ["rows"] = HostAdminViews.Vms(await client.VmsAsync(new() { ["kind"] = "all" }, ct), clock.UtcNow), ["childrenFeature"] = m.State["features"]?["children"]?.DeepClone() }; break;
                case "users": m.State["users"] = new JsonObject { ["rows"] = HostAdminViews.Map(await client.UsersAsync(ct), HostAdminViews.User) }; break;
                case "media": m.State["media"] = new JsonObject { ["catalog"] = HostAdminViews.IsoCatalog(await client.IsoCatalogAsync(ct)), ["catalogProblem"] = "", ["items"] = StateJson.Boolean(m.State["features"]?["media"]) == true ? HostAdminViews.Map(await client.MediaAsync(null, ct), HostAdminViews.Media) : new JsonArray(), ["mediaProblem"] = "" }; break;
                case "operations": m.State["operations"] = new JsonObject { ["jobs"] = HostAdminViews.Map(await client.JobsAsync(new() { ["limit"] = 100 }, ct), HostAdminViews.Job), ["audit"] = HostAdminViews.Map(await client.AuditAsync(new() { ["limit"] = 50 }, ct), HostAdminViews.Audit), ["auditProblem"] = "" }; break;
                case "config":
                    var config = await client.HostConfigAsync(ct);
                    m.State["config"] = new JsonObject { ["sections"] = HostAdminViews.Config(config), ["capabilities"] = HostAdminViews.Capabilities(await client.HostCapabilitiesAsync(ct)), ["problems"] = new JsonArray() }; break;
                case "maintenance": m.State["maintenanceTab"] = HostAdminViews.Updates(await client.UpdatesStatusAsync(ct)); break;
            }
            m.State["lastKnownAt"] = clock.UtcNow.ToString("O");
        }
        finally { m.State["busy"] = false; }
    }
    private async Task Action(Model m, RemoteHostClient client, string action, JsonObject args, CancellationToken ct)
    {
        if (action is "loadVmSettings" or "setVmSettings")
        { await VmSettingsAction(m, client, action, args, ct); return; }
        if (Text(m.State["mode"]) != "admin" && action is not ("shutdownVm" or "deleteVm" or "cancelJob" or "createFirstVm")) { Notice(m, "Not an administrator of this host."); return; }
        if (m.State["maintenance"] is not null && action is not ("refresh" or "updatesApply" or "updatesResolve")) { Notice(m, "The host is updating; mutations are disabled until it is back."); return; }
        var name = Text(args["name"]); var id = Text(args["id"]); JsonNode? result = null;
        m.State["notice"] = null;
        if (ActionConfirmation(action, args, client.Host) is { } confirmation && !await prompts.ConfirmAsync(confirmation.Title, confirmation.Detail, ct)) return;
        switch (action)
        {
            case "loadVmCpu":
                result = await client.VmCpuAsync(name, ct);
                events.HostAdmin(m.Host.Slug, new { type = "hostadmin.vmCpu", name, cpu = result }); return;
            case "changeVmCpu":
            {
                if (name.Length == 0) return;
                var cpu = await client.VmCpuAsync(name, ct);
                var cpuInput = await prompts.InputAsync(new($"CPU count for {name}",
                    $"Current: {Text(cpu?["currentCpus"])}. Allowed maximum: {Text(cpu?["maximumCpus"])}. Enter a count or max. Applies on the next full stop/start; an Ubuntu reboot is insufficient.",
                    StateJson.Boolean(cpu?["pending"]) == true ? Text(cpu?["desiredCpus"]) : "max"), ct);
                if (cpuInput is null) return;
                var cpus = cpuInput.Trim().Equals("max", StringComparison.OrdinalIgnoreCase) ? StateJson.CoerceNumber(cpu?["recommendedCpus"]) : StateJson.CoerceNumber(JsonValue.Create(cpuInput));
                if (!double.IsFinite(cpus) || cpus != Math.Floor(cpus) || cpus < 1 || !(cpus <= StateJson.CoerceNumber(cpu?["maximumCpus"])))
                { Notice(m, $"Enter max or a whole number from 1 to {Text(cpu?["maximumCpus"])}."); return; }
                await SetCpu(m, client, name, cpus, ct); return;
            }
            case "setVmCpu": await SetCpu(m, client, name, StateJson.CoerceNumber(args["cpus"]), ct); return;
            case "restartVm": case "startVm":
                result = await client.LifecycleAsync(name, new JsonObject { ["action"] = action == "restartVm" ? "restart" : "start" }, ct);
                m.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = $"{name}: {(action == "restartVm" ? "restart" : "start")} requested." }; return;
            case "refresh": await Detect(m, client, ct); break;
            case "capacityRefresh": result = await client.HostCapacityAsync(true, ct); break;
            case "shutdownVm": result = await client.LifecycleAsync(name, new JsonObject { ["action"] = "shutdown" }, ct); break;
            case "deleteVm":
                if (Text(args["kind"]) == "child")
                {
                    var row = (m.State["vms"]?["rows"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => Text(r["name"]) == name) ?? new JsonObject { ["name"] = name };
                    var childConfirm = ChildDeleteConfirmation(row);
                    if (!await prompts.ConfirmAsync(Text(childConfirm["title"]), Text(childConfirm["detail"]), ct)) return;
                }
                if (!await DeleteVm(m, client, name, ct)) return; break;
            case "shareVm": RequireScope(args); result = await client.SetVmSharingAsync(name, new JsonObject { ["scope"] = args["scope"]?.DeepClone() }, ct); break;
            case "changeVmLifetime": case "renewVmLease":
                var value = Text(args["lifetime"]);
                if (value.Length == 0) value = await prompts.InputAsync(new($"Change lifetime of {name}", "The expiry countdown starts again from now. Use 30m, 24h, 7d, or never. Host limits still apply.", "24h"), ct) ?? "";
                var lifetime = ParseLifetime(JsonValue.Create(value)); if (StateJson.Boolean(lifetime["ok"]) != true) { Notice(m, Text(lifetime["reason"])); return; }
                result = await client.RenewVmLeaseAsync(name, new JsonObject { ["lifetime"] = lifetime["text"]?.DeepClone() }, ct); break;
            case "cancelJob": result = await client.CancelJobAsync(id, ct); break;
            case "mediaCleanup": result = await client.MediaCleanupAsync(ct); break;
            case "deleteMedia": result = await client.DeleteMediaAsync(id, ct); break;
            case "createUser": result = await client.CreateUserAsync(RequireForm("newUser", args), ct); break;
            case "updateUser": result = await client.UpdateUserAsync(name, RequireForm("user", args), ct); break;
            case "deleteUser": result = await client.DeleteUserAsync(name, ct); break;
            case "saveAllowance": result = await client.PutUserAllowanceAsync(name, RequireForm("allowance", args), ct); break;
            case "saveOverrides": result = await client.PutOverridesAsync(name, RequireForm("overrides", args), ct); break;
            case "clearOverrides": result = await client.DeleteOverridesAsync(name, ct); break;
            case "loadOverrides":
                result = await client.OverridesAsync(name, ct); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.overrides", name, stored = HostAdminViews.AllowanceForm(result?["stored"]), effective = HostAdminViews.AllowanceText(result?["effective"]), problems = Array.Empty<object>() }); return;
            case "listTokens":
                await PushTokens(m, client, name, ct); return;
            case "issueToken": case "rotateVmToken": await SecretAction(m, client, action, args, ct); return;
            case "revokeToken": result = await client.RevokeUserTokenAsync(name, id, ct); break;
            case "revokeVmToken": result = await client.RevokeVmTokenAsync(name, ct); break;
            case "saveConfig": result = await client.PutHostConfigAsync(RequireSections(args), ct); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.configSaved" }); break;
            case "updatesCheck": result = await client.UpdatesCheckAsync(new JsonObject { ["releaseTag"] = args["releaseTag"]?.DeepClone() }, ct); break;
            case "updatesStage": result = await client.UpdatesStageAsync(new JsonObject { ["releaseTag"] = args["releaseTag"]?.DeepClone() }, ct); break;
            case "updatesApply": result = await client.UpdatesApplyAsync(new JsonObject { ["updateId"] = args["updateId"]?.DeepClone() }, ct); break;
            case "updatesCancel": result = await client.UpdatesCancelAsync(new JsonObject { ["updateId"] = args["updateId"]?.DeepClone() }, ct); break;
            case "updatesResolve": RequireResolve(args); result = await client.UpdatesResolveAsync(new JsonObject { ["updateId"] = args["updateId"]?.DeepClone(), ["action"] = args["action"]?.DeepClone() }, ct); break;
            case "updatesUpdate": await StartUpdate(m, client, ct); break;
            case "createFirstVm": await CreateFirstVmAsync(m.Host.Slug, ct); await Load(m, client, ct); return;
            default: Notice(m, "Unknown host-administration action."); return;
        }
        m.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = "Host action completed." };
        await Load(m, client, ct);
    }
    private static async Task SetCpu(Model m, RemoteHostClient client, string name, double cpus, CancellationToken ct)
    {
        if (!double.IsFinite(cpus) || cpus != Math.Floor(cpus) || cpus < 1 || cpus > 64)
        { Notice(m, "Choose a whole CPU count from 1 to 64."); return; }
        var cpu = await client.SetVmCpuAsync(name, new JsonObject { ["cpus"] = cpus }, ct);
        m.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = StateJson.Boolean(cpu?["pending"]) == true
            ? $"{name}: {Text(cpu?["desiredCpus"])} vCPUs saved for the next full stop/start. The running VM is unchanged."
            : $"{name}: CPU setting matches the current {Text(cpu?["currentCpus"])} vCPUs." };
    }
    // Host update workflow: the pending operation is persisted before each API call so a
    // Companion restart can resume or clear it (HostUpdatePlanner decides, this executes).
    private async Task StartUpdate(Model model, RemoteHostClient client, CancellationToken ct)
    {
        var status = await client.UpdatesStatusAsync(ct) as JsonObject ?? [];
        var check = Text(status["current"]?["state"]) == "staged" ? null : await client.UpdatesCheckAsync(null, ct) as JsonObject;
        var pending = HostUpdatePlanner.PlanStart(status, check, "cc-" + Guid.NewGuid().ToString("N"));
        if (pending is null) { model.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = "The host is already on the latest release." }; return; }
        SavePending(model, pending); await AdvanceUpdate(model, client, ct);
    }
    private async Task AdvanceUpdate(Model model, RemoteHostClient client, CancellationToken ct)
    {
        if (model.State["updatePending"] is not JsonObject pending) return;
        try
        {
            var status = await client.UpdatesStatusAsync(ct) as JsonObject ?? [];
            model.State["maintenanceTab"] = HostAdminViews.Updates(status);
            var plan = HostUpdatePlanner.PlanAdvance(pending, status);
            switch (Text(plan["action"]))
            {
                case "stage":
                    var staged = await client.UpdatesStageAsync(plan["body"], ct);
                    if (Text(staged?["updateId"]).Length == 0) { Notice(model, "The host returned no update id. Check update details before retrying."); return; }
                    var next = pending.DeepClone().AsObject(); next["updateId"] = staged?["updateId"]?.DeepClone(); SavePending(model, next); break;
                case "apply": await client.UpdatesApplyAsync(plan["body"], ct); break;
                case "clear": SavePending(model, null); if (Text(plan["error"]).Length > 0) Notice(model, Text(plan["error"])); break;
            }
        }
        catch (RemoteApiException e) { if (HostUpdatePlanner.ClearPendingOnError(e.Status)) SavePending(model, null); Refusal(model, e); }
    }
    private void SavePending(Model model, JsonObject? value)
    {
        model.State["updatePending"] = value?.DeepClone();
        if (value is null) files.DeleteFile(PendingPath(model.Host.Slug));
        else files.WriteFileAtomic(PendingPath(model.Host.Slug), StateJson.Bytes(value));
    }
    private async Task<bool> DeleteVm(Model model, RemoteHostClient client, string name, CancellationToken ct)
    {
        string? cascade = null;
        for (var round = 0; round < 3; round++)
        {
            try { await client.DeleteVmAsync(name, cascade is null ? null : new JsonObject { ["cascade"] = new JsonObject { ["token"] = cascade } }, ct); return true; }
            catch (RemoteApiException e) when (e.Status == 409 && e.Code == "cascade-token-expired")
            { Notice(model, "The confirmation expired; delete again to confirm the current children."); return false; }
            catch (RemoteApiException e) when (e.Status == 409 && e.Code is "cascade-confirmation-required" or "cascade-scope-changed")
            {
                var confirmation = CascadeConfirmation(new() { ["primary"] = name, ["problem"] = e.Body?.DeepClone() });
                if (!await prompts.ConfirmAsync(Text(confirmation["title"]), Text(confirmation["detail"]), ct)) return false;
                var typed = await prompts.InputAsync(new(Text(confirmation["confirmLabel"]), $"Type \"{name}\" to delete it and ALL its children ({Text(confirmation["sharedCount"])} shared host-wide). Disks, saved state and dedicated media are removed permanently."), ct);
                if (typed?.Trim() != name) return false;
                cascade = Text(confirmation["cascadeToken"]); if (cascade.Length == 0) { Notice(model, "The host returned no cascade confirmation token."); return false; }
            }
        }
        Notice(model, $"The set of children of \"{name}\" keeps changing; nothing was deleted. Try again when it is stable."); return false;
    }

    private static string RequireTab(string tab) => Tabs.Contains(tab) ? tab : throw new IpcFailure(400, "invalidTab", "Unknown administration tab.");
    private static JsonObject RequireForm(string kind, JsonObject args)
    {
        var parsed = ParseForm(kind, args["form"] as JsonObject ?? []);
        if (StateJson.Boolean(parsed["ok"]) != true) throw new IpcFailure(400, "invalidForm", "The host administration form contains invalid values.");
        return parsed["body"]!.AsObject();
    }
    // Validation reads trimmed text (as hostadmin.js does); the request bodies below carry the client's raw fields.
    private static void RequireScope(JsonObject args)
    {
        if (Text(args["scope"]) is not ("host" or "private")) throw new IpcFailure(400, "invalidScope", "Choose private or public on this host.");
    }
    private static void RequireResolve(JsonObject args)
    {
        if (Text(args["action"]) is not ("commit" or "abort" or "close")) throw new IpcFailure(400, "invalidResolve", "Resolve action must be commit, abort or close.");
    }
    private static JsonObject RequireSections(JsonObject args)
    {
        var invalid = new IpcFailure(400, "invalidConfig", "Each configuration section must contain a JSON object.");
        if (args["sections"] is not JsonArray sections || sections.Count == 0) throw invalid;
        var body = new JsonObject();
        foreach (var node in sections)
        {
            if (node is not JsonObject section) throw invalid;
            var key = Text(section["key"]); var parsed = StateJson.ParseObject(Text(section["text"]));
            if (!ConfigSections.Contains(key) || parsed is null) throw invalid;
            if (Text(section["expectedUpdatedAt"]).Length > 0) parsed["expectedUpdatedAt"] = section["expectedUpdatedAt"]?.DeepClone();
            body[key] = parsed;
        }
        return body;
    }
    private string HostsPath => Path.Combine(settings.Directory, "hosts.json");
    private string PendingPath(string slug) => Path.Combine(settings.Directory, "host-update-" + slug + ".json");
    private JsonArray Enrolled => HostIdentity.EnrolledHosts(files, settings.Directory);
    private HostRecord Find(string slug) => List().FirstOrDefault(h => h.Slug == slug) ?? throw new IpcFailure(404, "hostNotFound", "Unknown enrolled host.");
    private RemoteHostClient Client(HostRecord host) => new(api, files, tokens, host.Url, host.Auth == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate);
    private Model Get(string slug)
    { var host = Find(slug); return models.GetOrAdd(slug, _ => { var m = new Model(host); m.State["updatePending"] = StateJson.ReadObject(files, PendingPath(slug)); m.View = m.State.DeepClone().AsObject(); return m; }); }
    private static void Notice(Model m, string text) => m.State["notice"] = new JsonObject { ["level"] = "error", ["text"] = text };
    private static void Refusal(Model m, RemoteApiException e)
    {
        if (e.Status == 401) m.State["mode"] = "sign-in";
        else if (e.Status == 403 && e.Code is not ("lifetime-not-allowed" or "sharing-not-allowed")) m.State["mode"] = "user";
        else if (e.Status == 0) { m.State["mode"] = "unavailable"; m.State["retryable"] = true; }
        else if (e.Status == 503 && e.Code is "" or "maintenance") m.State["maintenance"] = new JsonObject { ["phase"] = "maintenance" };
        Notice(m, "The host refused this operation or is unavailable.");
    }
    private void Publish(Model m) { m.View = m.State.DeepClone().AsObject(); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.state", state = m.View }); }
    // State is mutated under Serial; View is the last published copy read without the lock.
    private sealed class Model(HostRecord host)
    {
        public HostRecord Host = host;
        public volatile bool Opened;
        public int Visibility;
        public JsonObject View = new() { ["mode"] = "unavailable" };
        public DateTimeOffset LastPoll;
        public DateTimeOffset LastDetection;
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public JsonObject State { get; } = new() { ["host"] = new Uri(host.Url).Host, ["url"] = host.Url, ["mode"] = "unavailable", ["message"] = "", ["retryable"] = false, ["maintenance"] = null, ["features"] = Features(null), ["identity"] = null, ["tabs"] = TabsFor(Features(null)), ["activeTab"] = "overview", ["busy"] = false, ["notice"] = null, ["overview"] = null, ["vms"] = null, ["users"] = null, ["media"] = null, ["operations"] = null, ["config"] = null, ["maintenanceTab"] = null, ["lastKnownAt"] = null, ["problem"] = "", ["updatePending"] = null };
    }
}
