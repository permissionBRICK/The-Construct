using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
namespace Construct.Companion.Core.Vault;

// One request of the tray pop-out's page ({type:"approvals.<action>", …}), validated by VaultApprovals.Parse.
public sealed record VaultApprovalsCommand(string Action, string Id = "", bool Approve = false, int Height = 0);
// What the window does after the records changed: Show = an approval arrived that was not listed before,
// Hide = none is left. Count feeds the tray menu.
public sealed record VaultApprovalsChange(bool Show, bool Hide, int Count);

// The tray pop-out's decisions (media/approvals.html): which approvals it lists and in what order, when it
// shows and hides itself, when an item's Approve is armed, and what the page may ask. The window only renders
// and forwards: it calls Sync on every VaultService.ApprovalsChanged, pushes StateMessage on every change and
// once a second while visible (the time left), and hands the page's requests to Parse and DecideAsync, in
// process. The page gets each approval's texts, the VM and the time left, never a value, and none of its
// requests reaches the message dispatcher, IPC or HTTP.
public sealed class VaultApprovals(VaultService vault, IClock clock)
{
    // Approve becomes clickable one second after the page first shows an item, so a click or key meant for
    // something else cannot approve a request that just appeared.
    public static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(1), RefreshInterval = TimeSpan.FromSeconds(1);
    // The page's own height in CSS pixels at 96 dpi: at least a header and one short item, at most the launcher popup.
    public const int MinHeight = 160;
    private const string Prefix = "approvals.";
    public static IReadOnlySet<string> Actions { get; } = new HashSet<string>(StringComparer.Ordinal) { "ready", "decide", "hide", "size", "openVault" };
    private readonly object gate = new();
    private readonly HashSet<string> known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> shownAt = new(StringComparer.Ordinal);

    public int Count => vault.PendingApprovals().Count;

    public VaultApprovalsChange Sync()
    {
        var current = vault.PendingApprovals();
        var ids = current.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            var arrived = !ids.IsSubsetOf(known);
            known.Clear(); known.UnionWith(ids);
            foreach (var gone in shownAt.Keys.Where(id => !ids.Contains(id)).ToArray()) shownAt.Remove(gone);
            return new(arrived, ids.Count == 0, ids.Count);
        }
    }

    // ── the page's state ───────────────────────────────────────────────────────
    // Oldest first. An item handed to the page for the first time starts its arming delay now.
    public JsonObject StateMessage()
    {
        var now = clock.UtcNow; var items = new JsonArray();
        var current = vault.PendingApprovals();
        lock (gate)
        {
            foreach (var a in current)
            {
                if (!shownAt.TryGetValue(a.Id, out var shown)) shownAt[a.Id] = shown = now;
                var armIn = shown + ArmDelay - now;
                items.Add(new JsonObject
                {
                    ["id"] = a.Id, ["vm"] = a.Vm, ["where"] = a.Kind == "host" ? "on " + (a.HostName.Length > 0 ? a.HostName : a.Host) : "this PC",
                    ["title"] = a.Title, ["message"] = a.Message, ["action"] = a.Action, ["deny"] = a.Deny,
                    ["names"] = new JsonArray(a.Names.Select(n => (JsonNode)n).ToArray()), ["left"] = Left(a.Deadline, now),
                    ["armIn"] = armIn > TimeSpan.Zero ? (long)Math.Ceiling(armIn.TotalMilliseconds) : 0
                });
            }
        }
        return new JsonObject { ["type"] = "approvals.state", ["items"] = items };
    }
    // Sent after every decision, so the page can release the item's buttons and show why it failed.
    public static JsonObject DoneMessage(string id, string? notice) => new() { ["type"] = "approvals.done", ["id"] = id, ["notice"] = notice };
    public static string Left(DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (deadline is not { } due) return "";
        var left = due - now;
        if (left <= TimeSpan.Zero) return "ending";
        if (left < TimeSpan.FromMinutes(1)) return $"{Math.Ceiling(left.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s left";
        return $"{Math.Ceiling(left.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min left";
    }
    // The window's height in device pixels for the page's reported height (CSS pixels): between MinHeight and
    // the launcher popup's height.
    public static int WindowHeight(int pageHeight, int dpi)
    {
        var max = TrayModel.PopupSize(96).Height;
        return (int)(Math.Clamp(pageHeight, MinHeight, max) * dpi / 96d); // scaled like TrayModel.PopupSize
    }

    // ── the page's requests ────────────────────────────────────────────────────
    // The page is untrusted input like any webview: an unknown type or a malformed field is refused here.
    public static VaultApprovalsCommand Parse(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() is not { } kind || !kind.StartsWith(Prefix, StringComparison.Ordinal) || !Actions.Contains(kind[Prefix.Length..]))
            throw new ArgumentException("Unknown key vault request.");
        var action = kind[Prefix.Length..];
        switch (action)
        {
            case "decide":
                if (!message.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !VaultProtocol.IsApprovalId(id.GetString()))
                    throw new ArgumentException("The request names no approval.");
                if (!message.TryGetProperty("decision", out var decision) || decision.ValueKind != JsonValueKind.String || decision.GetString() is not ("approve" or "deny"))
                    throw new ArgumentException("The decision must be approve or deny.");
                return new(action, id.GetString()!, decision.GetString() == "approve");
            case "size":
                if (!message.TryGetProperty("height", out var height) || height.ValueKind != JsonValueKind.Number || !height.TryGetInt32(out var pixels) || pixels is < 0 or > 10000)
                    throw new ArgumentException("The page height is invalid.");
                return new(action, Height: pixels);
            default: return new(action);
        }
    }

    // Answers one item. Null = done; otherwise the text the page shows on that item. The id must still be
    // listed (the page may be a moment behind), and Approve must be armed.
    public async Task<string?> DecideAsync(VaultApprovalsCommand command, CancellationToken cancellationToken)
    {
        if (command.Action != "decide") throw new ArgumentException("Not a decision.");
        if (!vault.PendingApprovals().Any(a => a.Id == command.Id)) return "This request was already answered or has ended.";
        if (command.Approve)
        {
            DateTimeOffset? shown; lock (gate) shown = shownAt.TryGetValue(command.Id, out var at) ? at : null;
            if (shown is null || clock.UtcNow < shown.Value + ArmDelay) return "Approve is not available yet. Try again.";
        }
        return await vault.DecideAsync(command.Id, command.Approve, cancellationToken: cancellationToken).ConfigureAwait(false) switch
        {
            VaultDecision.Decided => null,
            VaultDecision.AlreadyDecided => "This request was answered elsewhere first.",
            VaultDecision.NotFound => "This request was already answered or has ended.",
            _ => "The host did not take your answer. The Key Vault's Activity tab says why."
        };
    }
}
