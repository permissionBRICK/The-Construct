using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using static Construct.Companion.Core.HostAdmin.HostAdminProtocol;
namespace Construct.Companion.Host.Dispatch;
public sealed partial class HostAdministration
{
    private string PendingPath(string slug) => Path.Combine(settings.Directory, "host-update-" + slug + ".json");
    private void SavePending(Model model, JsonObject? value)
    {
        model.State["updatePending"] = value?.DeepClone();
        if (value is null) files.DeleteFile(PendingPath(model.Host.Slug));
        else files.WriteFileAtomic(PendingPath(model.Host.Slug), StateJson.Bytes(value));
    }
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
}
