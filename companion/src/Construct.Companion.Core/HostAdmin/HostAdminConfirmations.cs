using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.HostAdmin;
public static partial class HostAdminProtocol
{
    public static string? CascadeKind(JsonObject error) => StateJson.Number(error["status"]) != 409 ? null : Text(error["code"]) switch
    { "cascade-confirmation-required" => "required", "cascade-scope-changed" => "scope-changed", "cascade-token-expired" => "expired", _ => null };
    public static JsonObject CascadeConfirmation(JsonObject input)
    {
        var primary = Text(input["primary"]); if (primary.Length == 0) primary = "this VM";
        var problem = input["problem"] as JsonObject ?? []; var body = problem["body"] as JsonObject ?? problem;
        var children = new JsonArray();
        foreach (var node in body["children"] as JsonArray ?? [])
        {
            var child = node as JsonObject ?? new() { ["name"] = node?.DeepClone() };
            var sharing = Text(child["sharing"]).ToLowerInvariant(); if (sharing.Length == 0) sharing = "private";
            var state = Text(child["state"]).ToLowerInvariant(); if (state.Length == 0) state = "unknown";
            double? Number(string key) { var n = child[key] is null ? double.NaN : StateJson.CoerceNumber(child[key]); return double.IsFinite(n) ? n : null; }
            children.Add(new JsonObject { ["name"] = Text(child["name"]), ["state"] = state, ["sharing"] = sharing, ["shared"] = sharing == "host", ["diskGb"] = Number("diskGb"), ["mediaCount"] = Number("mediaCount") ?? 0 });
        }
        var n = children.Count; var suffix = n == 1 ? "" : "s";
        var lines = new List<string> { $"Deleting \"{primary}\" also deletes ALL of its {n} child VM{suffix}, including shared ones.", "Their virtual disks, saved state and dedicated media are removed permanently.", "" };
        foreach (var child in children)
            lines.Add("  " + Text(child!["name"]) + "   " + Text(child["state"]) + "   " + (StateJson.Boolean(child["shared"]) == true ? "SHARED HOST-WIDE (other users may be using it)" : "private") + (child["diskGb"] is null ? "" : "   " + Text(child["diskGb"]) + " GB disk") + (StateJson.Number(child["mediaCount"]) != 0 ? "   " + Text(child["mediaCount"]) + " dedicated media" : ""));
        lines.Add(""); lines.Add("Type the instance name to confirm." + (Text(body["expiresAt"]).Length == 0 ? "" : " (This confirmation expires at " + FormatWhen(body["expiresAt"]) + ".)"));
        return new() { ["kind"] = Text(body["code"]) == "cascade-scope-changed" ? "scope-changed" : "required", ["title"] = $"Delete \"{primary}\" and its {n} child VM{suffix}?", ["detail"] = string.Join('\n', lines), ["children"] = children, ["sharedCount"] = children.Count(c => StateJson.Boolean(c!["shared"]) == true), ["cascadeToken"] = Text(body["cascadeToken"]), ["expiresAt"] = Text(body["expiresAt"]), ["requireTypedName"] = true, ["typedName"] = primary, ["confirmLabel"] = n == 0 ? $"Delete {primary}" : $"Delete {primary} and {n} child VM{suffix}" };
    }
    public static (string Title, string Detail)? ActionConfirmation(string action, JsonObject args, string host)
    {
        var name = Text(args["name"]); var id = Text(args["id"]); var update = Text(args["updateId"]);
        return action switch
        {
            "restartVm" => ($"Restart \"{name}\"?", "Construct will ask Ubuntu to shut down, apply any pending CPU count, then start the VM. Running work will be interrupted."),
            "startVm" => ($"Start \"{name}\"?", "Construct will apply any pending CPU count before starting this powered-off VM."),
            "shutdownVm" => ($"Request a graceful shutdown of \"{name}\"?", "The guest is asked to shut down (like the instance panel's Shutdown). The service never forces it off; if the guest offers no shutdown integration, that is reported."),
            "cancelJob" => ($"Cancel job {id}?", "A job that has already reached a point of no return finishes anyway; the service answers whether it was cancelled."),
            "deleteMedia" => ($"Delete the media item \"{(name.Length == 0 ? id : name)}\"?", "The file is removed from the host and its storage reservation released. Items referenced by a VM are refused by the service."),
            "deleteUser" => ($"Remove the user \"{name}\" from {host}?", "Refused by the service while the user still owns VMs (children included). Their tokens stop working immediately."),
            "revokeToken" => ($"Revoke token {id} of \"{name}\"?", "The next request with it is refused."),
            "revokeVmToken" => ($"Revoke the VM token of \"{name}\"?", $"The guest loses expose and heartbeat. Restore its credential with Provision-AgentVM.ps1 -InstanceName {name} -RotateVmToken."),
            "clearOverrides" => ($"Clear the overrides of \"{name}\"?", "The owner's allowance applies again unrestricted."),
            "updatesApply" => (StateJson.Boolean(args["resume"]) == true ? $"Resume the interrupted update {update}?" : $"Apply update {update} to {host}?", "The service drains its own host jobs (not guest provisioning), hands off to the updater, stops, is replaced and restarted. Guest VMs keep running. Clients reconnect afterwards."),
            "updatesCancel" => ($"Cancel update {update}?", "Only possible before the hand-off to the updater."),
            "updatesResolve" => ($"Resolve update {update} with \"{Text(args["action"]).ToLowerInvariant()}\"?", "commit: keep the new build (only when it really runs and verifies). abort: restore the previous build from its complete backup. close: record that the installation is the previous one. The service refuses a resolution its own checks contradict."),
            _ => null
        };
    }
}
