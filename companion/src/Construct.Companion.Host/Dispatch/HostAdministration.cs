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

public sealed partial class HostAdministration(IStateFileSystem files, ITokenStore tokens, IRemoteApi api,
    IPrompts prompts, IClock clock, IpcSettings settings, IpcEvents events, RuntimeMessageBus bus, CompanionInstances instances)
{
    private readonly SemaphoreSlim enrollment = new(1, 1);
    private readonly ConcurrentDictionary<string, Model> models = new(StringComparer.Ordinal);
    private string HostsPath => Path.Combine(settings.Directory, "hosts.json");
    private JsonArray Enrolled => StateJson.ReadObject(files, HostsPath)?["hosts"] as JsonArray ?? [];
    public IReadOnlyList<HostRecord> List()
    {
        var hosts = Enrolled.OfType<JsonObject>().ToDictionary(h => HostIdentity.HostSlug(Text(h["url"])), h => h);
        foreach (var definition in instances.Registry.List())
            if (definition["service"] is JsonObject service && Text(service["url"]).Length > 0) hosts.TryAdd(HostIdentity.HostSlug(Text(service["url"])), service);
        return hosts.Select(h => new HostRecord(h.Key, HostIdentity.NormalizeServiceUrl(Text(h.Value["url"])), Text(h.Value["auth"]) == "token" ? "token" : "negotiate", HostIdentity.ReadPin(files, Text(h.Value["url"])).Length > 0, models.TryGetValue(h.Key, out var model) ? Text(model.View["mode"]) == "admin" : null)).ToArray();
    }
    private HostRecord Find(string slug) => List().FirstOrDefault(h => h.Slug == slug) ?? throw new IpcFailure(404, "hostNotFound", "Unknown enrolled host.");
    private RemoteHostClient Client(HostRecord host) => new(api, files, tokens, host.Url, host.Auth == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate);
    private Model Get(string slug)
    { var host = Find(slug); return models.GetOrAdd(slug, _ => { var m = new Model(host); m.State["updatePending"] = StateJson.ReadObject(files, PendingPath(slug)); m.View = m.State.DeepClone().AsObject(); return m; }); }
    public JsonObject Snapshot(string slug) => new() { ["type"] = "hostadmin.state", ["state"] = Get(slug).View.DeepClone() };
    public async Task<HostRecord> AddAsync(AddRemoteHost input, CancellationToken ct)
    {
        await enrollment.WaitAsync(ct);
        try
        {
            string url;
            try { url = HostIdentity.NormalizeServiceUrl(input.Url); HostIdentity.AssertTransportSafe(url); }
            catch { throw new IpcFailure(400, "invalidHost", "A secure host URL is required."); }
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
    public void Validate(string slug, JsonObject message)
    {
        Find(slug); var type = Text(message["type"]);
        if (type.Length == 0) throw new IpcFailure(400, "invalidMessage", "A message type is required.");
        if (type == "hostadmin.tab" && !Tabs.Contains(Text(message["tab"]))) throw new IpcFailure(400, "invalidTab", "Unknown administration tab.");
        if (type != "hostadmin.action") return;
        var action = Text(message["action"]); var args = message["args"] as JsonObject ?? [];
        if (action.Length == 0) throw new IpcFailure(400, "invalidMessage", "An action is required.");
        var form = action switch { "createUser" => "newUser", "updateUser" => "user", "saveAllowance" => "allowance", "saveOverrides" => "overrides", _ => null };
        if (form is not null && StateJson.Boolean(ParseForm(form, args["form"] as JsonObject ?? [])["ok"]) != true) throw new IpcFailure(400, "invalidForm", "The host administration form contains invalid values.");
        if (action == "shareVm" && Text(args["scope"]) is not ("host" or "private")) throw new IpcFailure(400, "invalidScope", "Choose private or public on this host.");
        if (action == "updatesResolve" && Text(args["action"]) is not ("commit" or "abort" or "close")) throw new IpcFailure(400, "invalidResolve", "Resolve action must be commit, abort or close.");
        if (action == "saveConfig")
        {
            var sections = args["sections"] as JsonArray;
            if (sections is null || sections.Count == 0 || sections.Any(n => n is not JsonObject o || !ConfigSections.Contains(Text(o["key"])) || StateJson.ParseObject(Text(o["text"])) is null)) throw new IpcFailure(400, "invalidConfig", "Each configuration section must contain a JSON object.");
        }
    }
    public async Task DispatchAsync(string slug, JsonObject message, CancellationToken ct)
    {
        var model = Get(slug); await model.Serial.WaitAsync(ct);
        try
        {
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
                case "hostadmin.tab":
                    if (!Tabs.Contains(Text(message["tab"]))) throw new IpcFailure(400, "invalidTab", "Unknown administration tab.");
                    model.State["activeTab"] = Text(message["tab"]); await Load(model, client, ct); break;
                case "hostadmin.action": await Action(model, client, Text(message["action"]), message["args"] as JsonObject ?? [], ct); break;
                default: Notice(model, "This host-administration message is unsupported."); break;
            }
            model.Opened = true;
        }
        catch (RemoteApiException e) { Refusal(model, e); }
        catch (IpcFailure e) { Notice(model, e.Message); throw; }
        finally { Publish(model); model.Serial.Release(); }
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
                    var sections = new JsonArray(); foreach (var key in ConfigSections) if (config?[key] is { } value) sections.Add(new JsonObject { ["key"] = key, ["text"] = value.ToJsonString(new() { WriteIndented = true }), ["updatedAt"] = value["updatedAt"]?.DeepClone() });
                    m.State["config"] = new JsonObject { ["sections"] = HostAdminViews.Config(config), ["capabilities"] = HostAdminViews.Capabilities(await client.HostCapabilitiesAsync(ct)), ["problems"] = new JsonArray() }; break;
                case "maintenance": m.State["maintenanceTab"] = HostAdminViews.Updates(await client.UpdatesStatusAsync(ct)); break;
            }
            m.State["lastKnownAt"] = clock.UtcNow.ToString("O");
        }
        finally { m.State["busy"] = false; }
    }
    private async Task Action(Model m, RemoteHostClient client, string action, JsonObject args, CancellationToken ct)
    {
        if (Text(m.State["mode"]) != "admin" && action is not ("shutdownVm" or "deleteVm" or "cancelJob")) { Notice(m, "Not an administrator of this host."); return; }
        if (m.State["maintenance"] is not null && action is not ("refresh" or "updatesApply" or "updatesResolve")) { Notice(m, "The host is updating; mutations are disabled until it is back."); return; }
        var name = Text(args["name"]); var id = Text(args["id"]); JsonNode? result = null;
        m.State["notice"] = null;
        if (ActionConfirmation(action, args, client.Host) is { } confirmation && !await prompts.ConfirmAsync(confirmation.Title, confirmation.Detail, ct)) return;
        JsonObject Form(string kind)
        { var parsed = ParseForm(kind, args["form"] as JsonObject ?? []); if (StateJson.Boolean(parsed["ok"]) != true) throw new IpcFailure(400, "invalidForm", "The host administration form contains invalid values."); return parsed["body"]!.AsObject(); }
        switch (action)
        {
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
            case "shareVm": if (Text(args["scope"]) is not ("host" or "private")) throw new IpcFailure(400, "invalidScope", "Choose private or public on this host."); result = await client.SetVmSharingAsync(name, new JsonObject { ["scope"] = args["scope"]?.DeepClone() }, ct); break;
            case "changeVmLifetime": case "renewVmLease":
                var value = Text(args["lifetime"]);
                if (value.Length == 0) value = await prompts.InputAsync(new($"Change lifetime of {name}", "The expiry countdown starts again from now. Use 30m, 24h, 7d, or never. Host limits still apply.", "24h"), ct) ?? "";
                var lifetime = ParseLifetime(JsonValue.Create(value)); if (StateJson.Boolean(lifetime["ok"]) != true) { Notice(m, Text(lifetime["reason"])); return; }
                result = await client.RenewVmLeaseAsync(name, new JsonObject { ["lifetime"] = lifetime["text"]?.DeepClone() }, ct); break;
            case "cancelJob": result = await client.CancelJobAsync(id, ct); break;
            case "mediaCleanup": result = await client.MediaCleanupAsync(ct); break;
            case "deleteMedia": result = await client.DeleteMediaAsync(id, ct); break;
            case "createUser": result = await client.CreateUserAsync(Form("newUser"), ct); break;
            case "updateUser": result = await client.UpdateUserAsync(name, Form("user"), ct); break;
            case "deleteUser": result = await client.DeleteUserAsync(name, ct); break;
            case "saveAllowance": result = await client.PutUserAllowanceAsync(name, Form("allowance"), ct); break;
            case "saveOverrides": result = await client.PutOverridesAsync(name, Form("overrides"), ct); break;
            case "clearOverrides": result = await client.DeleteOverridesAsync(name, ct); break;
            case "loadOverrides":
                result = await client.OverridesAsync(name, ct); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.overrides", name, stored = HostAdminViews.AllowanceForm(result?["stored"]), effective = HostAdminViews.AllowanceText(result?["effective"]), problems = Array.Empty<object>() }); return;
            case "listTokens":
                result = await client.UserTokensAsync(name, ct); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.tokens", name, tokens = result }); return;
            case "issueToken": case "rotateVmToken": Notice(m, "One-time secret display is not implemented by the desktop prompt seam. Use the host CLI to issue or rotate tokens."); return;
            case "revokeToken": result = await client.RevokeUserTokenAsync(name, id, ct); break;
            case "revokeVmToken": result = await client.RevokeVmTokenAsync(name, ct); break;
            case "saveConfig":
                var body = new JsonObject(); foreach (var section in (args["sections"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var key = Text(section["key"]); var parsed = StateJson.ParseObject(Text(section["text"]));
                    if (!ConfigSections.Contains(key) || parsed is null) throw new IpcFailure(400, "invalidConfig", "Each configuration section must contain a JSON object.");
                    if (Text(section["expectedUpdatedAt"]).Length > 0) parsed["expectedUpdatedAt"] = section["expectedUpdatedAt"]?.DeepClone(); body[key] = parsed;
                }
                if (body.Count == 0) throw new IpcFailure(400, "invalidConfig", "No configuration section was changed.");
                result = await client.PutHostConfigAsync(body, ct); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.configSaved" }); break;
            case "updatesCheck": result = await client.UpdatesCheckAsync(new JsonObject { ["releaseTag"] = args["releaseTag"]?.DeepClone() }, ct); break;
            case "updatesStage": result = await client.UpdatesStageAsync(new JsonObject { ["releaseTag"] = args["releaseTag"]?.DeepClone() }, ct); break;
            case "updatesApply": result = await client.UpdatesApplyAsync(new JsonObject { ["updateId"] = args["updateId"]?.DeepClone() }, ct); break;
            case "updatesCancel": result = await client.UpdatesCancelAsync(new JsonObject { ["updateId"] = args["updateId"]?.DeepClone() }, ct); break;
            case "updatesResolve":
                if (Text(args["action"]) is not ("commit" or "abort" or "close")) throw new IpcFailure(400, "invalidResolve", "Resolve action must be commit, abort or close.");
                result = await client.UpdatesResolveAsync(new JsonObject { ["updateId"] = args["updateId"]?.DeepClone(), ["action"] = args["action"]?.DeepClone() }, ct); break;
            case "updatesUpdate": await StartUpdate(m, client, ct); break;
            case "createFirstVm": Notice(m, "Use New Remote VM in VS Code; the creation wizard is not yet ported."); return;
            default: Notice(m, "Unknown host-administration action."); return;
        }
        m.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = "Host action completed." };
        await Load(m, client, ct);
    }
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
                    children = new JsonObject { ["primary"] = entry.Name, ["visible"] = true, ["items"] = HostAdminViews.Children(await client.ChildrenAsync(entry.Name, ct), clock.UtcNow), ["problem"] = "" };
                idle = IdlePolicy(await client.RequestAsync("GET", "/vms/" + HostIdentity.Encode(entry.Name) + "/idle-policy", cancellationToken: ct));
            }
            catch (RemoteApiException) { }
        }
        bus.Publish(entry.Name, new { type = "children", instance = entry.Name, children });
        bus.Publish(entry.Name, new { type = "hostAdminOffer", instance = entry.Name, offer });
        bus.Publish(entry.Name, new { type = "idlePolicy", instance = entry.Name, idlePolicy = idle });
    }
    public async Task ChildActionAsync(CompanionInstance entry, string action, string child, CancellationToken ct)
    {
        var snapshot = bus.Snapshot(entry.Name);
        var rows = snapshot.TryGetValue("children", out var cached) ? JsonNode.Parse(cached.GetRawText())?["children"]?["items"] as JsonArray : null;
        var row = rows?.OfType<JsonObject>().FirstOrDefault(r => Text(r["name"]) == child) ?? throw new IpcFailure(400, "unknownChild", "The child was not listed for this instance.");
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
    private static void Notice(Model m, string text) => m.State["notice"] = new JsonObject { ["level"] = "error", ["text"] = text };
    private static void Refusal(Model m, RemoteApiException e, bool detecting = false)
    {
        if (e.Status == 401) m.State["mode"] = "sign-in";
        else if (e.Status == 403 && e.Code is not ("lifetime-not-allowed" or "sharing-not-allowed")) m.State["mode"] = detecting ? "denied" : "user";
        else if (e.Status == 0) { m.State["mode"] = "unavailable"; m.State["retryable"] = true; }
        else if (e.Status == 503 && e.Code is "" or "maintenance") m.State["maintenance"] = new JsonObject { ["phase"] = "maintenance" };
        Notice(m, "The host refused this operation or is unavailable.");
    }
    private void Publish(Model m) { m.View = m.State.DeepClone().AsObject(); events.HostAdmin(m.Host.Slug, new { type = "hostadmin.state", state = m.View }); }
    private sealed class Model(HostRecord host)
    {
        public HostRecord Host = host;
        public volatile bool Opened;
        public JsonObject View = new() { ["mode"] = "unavailable" };
        public DateTimeOffset LastPoll;
        public DateTimeOffset LastDetection;
        public SemaphoreSlim Serial { get; } = new(1, 1);
        public JsonObject State { get; } = new() { ["host"] = new Uri(host.Url).Host, ["url"] = host.Url, ["mode"] = "unavailable", ["message"] = "", ["retryable"] = false, ["maintenance"] = null, ["features"] = Features(null), ["identity"] = null, ["tabs"] = TabsFor(Features(null)), ["activeTab"] = "overview", ["busy"] = false, ["notice"] = null, ["overview"] = null, ["vms"] = null, ["users"] = null, ["media"] = null, ["operations"] = null, ["config"] = null, ["maintenanceTab"] = null, ["lastKnownAt"] = null, ["problem"] = "", ["updatePending"] = null };
    }
}
