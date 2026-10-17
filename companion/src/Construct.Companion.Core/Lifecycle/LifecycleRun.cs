using System.Text.Json.Nodes;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Lifecycle;

// One lifecycle run (reprovision, reinstall, redownload, T3 Code update, Construct update, host conversion)
// at a time on this PC. The entry scripts hold lifecycle-running.json for their whole run
// (lib/AgentVm.LifecycleRun.ps1); the panel locks its lifecycle buttons for every instance while a run is
// preparing or running. The extension's lifecycle.parseRunMarker / runBusyReason apply the same rules.
public static class LifecycleRun
{
    public static string MarkerPath(string localAppData) => Path.Combine(localAppData, "The-Construct", "lifecycle-running.json");
    // The run while its owner process is alive; null for no marker, an unreadable one or a dead owner.
    public static JsonObject? Parse(string? text, Func<int, bool> isDead)
    {
        if (StateJson.ParseObject(text) is not { } marker || StateJson.Number(marker["pid"]) is not { } pid
            || pid < 1 || pid > int.MaxValue || pid != Math.Floor(pid) || isDead((int)pid)) return null;
        return Describe(StateJson.Text(marker["instance"]), StateJson.Text(marker["action"]) ?? "", "running", StateJson.Text(marker["startedAt"]));
    }
    // phase: "preparing" while a panel launch is checked and confirmed, "running" while its script holds the marker.
    public static JsonObject Describe(string? instance, string action, string phase, string? startedAt = null) =>
        new() { ["action"] = action, ["instance"] = string.IsNullOrEmpty(instance) ? null : instance, ["phase"] = phase, ["startedAt"] = startedAt };
    public static string BusyReason(JsonObject run)
    {
        var action = StateJson.Text(run["action"]) ?? "";
        var label = action switch
        {
            "reprovision" => "Reprovision", "reinstall" => "Reinstall", "redownload" => "Redownload",
            "updateT3" => "T3 Code update", "updateConstruct" => "Construct update", "convertToHost" => "Host conversion", _ => "A Construct run"
        };
        var instance = StateJson.Text(run["instance"]);
        var state = StateJson.Text(run["phase"]) == "preparing" ? "starting" : "running";
        return $"{label}{(instance is null ? "" : $" of “{instance}”")} is {state}. Wait until it finishes, then try again.";
    }
}
