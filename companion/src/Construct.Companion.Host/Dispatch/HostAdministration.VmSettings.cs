using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Host.Dispatch;

public sealed partial class HostAdministration
{
    private sealed class VmSettingsValidationException(string message) : Exception(message);

    private static async Task<JsonObject> ReadVmSettings(Model m, RemoteHostClient client, string name, CancellationToken ct) => new()
    {
        ["cpu"] = StateJson.Boolean(m.State["features"]?["primaryCpu"]) == true ? await client.VmCpuAsync(name, ct) : null,
        ["memory"] = StateJson.Boolean(m.State["features"]?["primaryMemory"]) == true ? await client.VmMemoryAsync(name, ct) : null,
        ["idle"] = await client.VmIdlePolicyAsync(name, ct)
    };

    private async Task VmSettingsAction(Model m, RemoteHostClient client, string action, JsonObject args, CancellationToken ct)
    {
        var name = Text(args["name"]);
        JsonObject? settings = null;
        var saved = false;
        var error = "";
        try
        {
            if (Text(m.State["mode"]) != "admin") throw new VmSettingsValidationException("Not an administrator of this host.");
            if (m.State["maintenance"] is not null) throw new VmSettingsValidationException("The host is updating; mutations are disabled until it is back.");
            settings = await ReadVmSettings(m, client, name, ct);
            if (action == "setVmSettings")
            {
                var cpu = settings["cpu"]; var memory = settings["memory"]; var idle = settings["idle"];
                static double Whole(JsonNode? value, double min, double max, string message)
                {
                    // Match the JS number-only gate, including rejecting null, strings and booleans.
                    var parsed = StateJson.CoerceNumber(value);
                    if (value?.GetValueKind() != JsonValueKind.Number || !double.IsFinite(parsed) || parsed != Math.Floor(parsed) || parsed < min || parsed > max)
                        throw new VmSettingsValidationException(message);
                    return parsed;
                }
                static double Hardware(JsonNode? value, JsonNode? desired, JsonNode? maximum, string message)
                {
                    var parsed = Whole(value, 1, int.MaxValue, message);
                    if (parsed != StateJson.CoerceNumber(desired) && parsed > StateJson.CoerceNumber(maximum)) throw new VmSettingsValidationException(message);
                    return parsed;
                }
                var cpus = cpu is null ? 0 : Hardware(args["cpus"], cpu["desiredCpus"], cpu["maximumCpus"], $"CPU count must be between 1 and {Text(cpu["maximumCpus"])}.");
                var ram = memory is null ? 0 : Hardware(args["ramGb"], memory["desiredRamGb"], memory["maximumRamGb"], $"RAM (GB) must be between 1 and {Text(memory["maximumRamGb"])}.");
                var forced = StateJson.Boolean(idle?["forceEnabled"]) == true;
                var cap = StateJson.CoerceNumber(idle?["maxTimeoutMinutes"]);
                const string idleError = "Choose an idle timeout and action within the host cap.";
                var timeout = Whole(args["timeoutMinutes"], forced ? 1 : 0, cap > 0 ? cap : int.MaxValue, idleError);
                var idleAction = Text(args["action"]);
                if (idleAction is not ("save" or "shutdown" or "off") || forced && idleAction == "off") throw new VmSettingsValidationException(idleError);
                if (cpu is not null && cpus != StateJson.CoerceNumber(cpu["desiredCpus"])) await client.SetVmCpuAsync(name, new JsonObject { ["cpus"] = cpus }, ct);
                if (memory is not null && ram != StateJson.CoerceNumber(memory["desiredRamGb"])) await client.SetVmMemoryAsync(name, new JsonObject { ["ramGb"] = ram }, ct);
                if (timeout != StateJson.CoerceNumber(idle?["timeoutMinutes"]) || idleAction != Text(idle?["action"]))
                    await client.SetVmIdlePolicyAsync(name, new JsonObject { ["timeoutMinutes"] = timeout, ["action"] = idleAction }, ct);
                saved = true;
                m.State["notice"] = new JsonObject { ["level"] = "info", ["text"] = $"{name}: settings saved. CPU and RAM apply on the next full stop/start; idle policy applies immediately." };
            }
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
