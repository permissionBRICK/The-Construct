using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Host.Dispatch;

public sealed partial class HostAdministration
{
    private sealed class VmSettingsValidationException(string message) : Exception(message);

    private static async Task<JsonObject> ReadVmSettings(Model m, RemoteHostClient client, string name, CancellationToken ct, JsonObject? changes = null)
    {
        async Task<(string Key, JsonNode? Value, string? Error)> Read(string key, bool enabled, Func<Task<JsonNode?>> fetch)
        {
            if (!enabled) return (key, null, null);
            try { return (key, await fetch(), null); }
            catch (RemoteApiException ex) when (changes is null && ex.Status is not (401 or 403))
            { return (key, null, $"{key} settings unavailable: {ex.Message}"); }
        }
        var results = await Task.WhenAll(
            Read("cpu", StateJson.Boolean(m.State["features"]?["primaryCpu"]) == true && (changes is null || changes.ContainsKey("cpus")), () => client.VmCpuAsync(name, ct)),
            Read("memory", StateJson.Boolean(m.State["features"]?["primaryMemory"]) == true && (changes is null || changes.ContainsKey("ramGb")), () => client.VmMemoryAsync(name, ct)),
            Read("idle", changes is null || changes.ContainsKey("timeoutMinutes") || changes.ContainsKey("action"), () => client.VmIdlePolicyAsync(name, ct)));
        var settings = new JsonObject(); var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value, error) in results)
        {
            settings[key] = value;
            if (error is not null) warnings.Add(error);
            if (value?["warnings"] is JsonArray items)
                foreach (var item in items) warnings.Add("Inventory warning: " + Text(item));
        }
        settings["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray());
        return settings;
    }

    private async Task VmSettingsAction(Model m, RemoteHostClient client, string action, JsonObject args, CancellationToken ct)
    {
        var name = Text(args["name"]);
        JsonObject? settings = null;
        var saved = false;
        var error = "";
        try
        {
            if (Text(m.State["mode"]) != "admin") throw new VmSettingsValidationException("Not an administrator of this host.");
            if (action == "setVmSettings" && m.State["maintenance"] is not null) throw new VmSettingsValidationException("The host is updating; mutations are disabled until it is back.");
            if (action == "setVmSettings")
            {
                static double Whole(JsonNode? value, double min, double max, string message)
                {
                    // Match the JS number-only gate, including rejecting null, strings and booleans.
                    var parsed = StateJson.CoerceNumber(value);
                    if (value?.GetValueKind() != JsonValueKind.Number || !double.IsFinite(parsed) || parsed != Math.Floor(parsed) || parsed < min || parsed > max)
                        throw new VmSettingsValidationException(message);
                    return parsed;
                }
                var changeCpu = StateJson.Boolean(m.State["features"]?["primaryCpu"]) == true && args.ContainsKey("cpus");
                var changeRam = StateJson.Boolean(m.State["features"]?["primaryMemory"]) == true && args.ContainsKey("ramGb");
                var changeIdle = args.ContainsKey("timeoutMinutes") || args.ContainsKey("action");
                var cpus = changeCpu ? Whole(args["cpus"], 1, 64, "Choose a whole CPU count from 1 to 64.") : 0;
                var ram = changeRam ? Whole(args["ramGb"], 1, 1024, "Choose whole RAM (GB) from 1 to 1024.") : 0;
                const string idleError = "Choose an idle timeout and action within the host cap.";
                var timeout = changeIdle ? Whole(args["timeoutMinutes"], 0, int.MaxValue, idleError) : 0;
                var idleAction = Text(args["action"]);
                if (changeIdle && idleAction is not ("save" or "shutdown" or "off")) throw new VmSettingsValidationException(idleError);
                settings = await ReadVmSettings(m, client, name, ct, args);
                var cpu = settings["cpu"]; var memory = settings["memory"]; var idle = settings["idle"];
                var forced = StateJson.Boolean(idle?["forceEnabled"]) == true;
                var cap = StateJson.CoerceNumber(idle?["maxTimeoutMinutes"]);
                if (idle is not null && (timeout < (forced ? 1 : 0) || cap > 0 && timeout > cap || forced && idleAction == "off")) throw new VmSettingsValidationException(idleError);
                if (cpu is not null && cpus != StateJson.CoerceNumber(cpu["desiredCpus"])) await client.SetVmCpuAsync(name, new JsonObject { ["cpus"] = cpus }, ct);
                if (memory is not null && ram != StateJson.CoerceNumber(memory["desiredRamGb"])) await client.SetVmMemoryAsync(name, new JsonObject { ["ramGb"] = ram }, ct);
                if (idle is not null && (timeout != StateJson.CoerceNumber(idle["timeoutMinutes"]) || idleAction != Text(idle["action"])))
                    await client.SetVmIdlePolicyAsync(name, new JsonObject { ["timeoutMinutes"] = timeout, ["action"] = idleAction }, ct);
                saved = true;
                m.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = $"{name}: settings saved. CPU and RAM apply on the next full stop/start; idle policy applies immediately." };
            }
            else settings = await ReadVmSettings(m, client, name, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RemoteApiException ex) { Refusal(m, ex); error = ex.Message; }
        catch (VmSettingsValidationException ex) { Notice(m, ex.Message); error = ex.Message; }
        catch (Exception) { error = "Could not load or save VM settings."; Notice(m, error); }
        if (error.Length > 0)
        {
            settings = null;
            if (action == "setVmSettings" && Text(m.State["mode"]) == "admin" && m.State["maintenance"] is null)
            {
                try { settings = await ReadVmSettings(m, client, name, ct); }
                catch (RemoteApiException ex) { Refusal(m, ex); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { Notice(m, "Could not reload VM settings."); }
            }
        }
        events.HostAdmin(m.Host.Slug, new { type = "hostadmin.vmSettings", name, requestId = args["requestId"]?.DeepClone(), saved, settings, error });
        if (saved) await Load(m, client, ct);
    }
}
