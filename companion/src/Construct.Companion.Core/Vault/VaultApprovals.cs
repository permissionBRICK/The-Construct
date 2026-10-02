using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
namespace Construct.Companion.Core.Vault;

// One request of the tray pop-out's page ({type:"approvals.<action>", …}), validated by VaultApprovals.Parse.
public sealed record VaultApprovalsCommand(string Action, string Id = "", bool Approve = false, int Height = 0);
// What the window does after Sync: Visible = the pop-out belongs on screen. Count feeds the tray menu and keeps
// the window's one-second re-evaluation running while it is above zero.
public sealed record VaultApprovalsVisibility(bool Visible, int Count);

// The tray pop-out's decisions (media/approvals.html): which approvals it lists and in what order, when it
// shows and hides itself, when an item's Approve is armed, and what the page may ask. The window only renders
// and forwards: it calls Sync on every VaultService.ApprovalsChanged and once a second while approvals wait,
// pushes StateMessage on every change and once a second while visible (the time left), and hands the page's
// requests to Parse and DecideAsync, in process. The page gets each approval's texts, the VM and the time left,
// never a value, and none of its requests reaches the message dispatcher, IPC or HTTP.
//
// The pop-out steps back for another app on this PC that shows the approvals itself: T3 Code Desktop reports
// on every poll while its window is visible which pending approvals it shows inline (Displayed, from
// POST /v1/vault/approvals/displayed; an empty list is a heartbeat). While no app reported for ReporterWindow,
// an approval is eligible at once. Otherwise it waits DisplayGrace from its arrival, then is eligible unless an
// app's mark on it is younger than DisplayedFor, so it shows once the reporter stops or leaves it out. The
// pop-out is visible while an eligible approval exists that the user did not close it for, and whenever the
// user brought it up (Open); it lists every pending approval.
public sealed class VaultApprovals(VaultService vault, IClock clock)
{
    // Approve becomes clickable one second after the page first shows an item, so a click or key meant for
    // something else cannot approve a request that just appeared.
    public static readonly TimeSpan ArmDelay = TimeSpan.FromSeconds(1), RefreshInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReporterWindow = TimeSpan.FromSeconds(10), DisplayGrace = TimeSpan.FromSeconds(5), DisplayedFor = TimeSpan.FromSeconds(8);
    public const int MaxDisplayed = 50;
    // The page's own height in CSS pixels at 96 dpi: at least a header and one short item, at most the launcher popup.
    public const int MinHeight = 160;
    private const string Prefix = "approvals.";
    public static IReadOnlySet<string> Actions { get; } = new HashSet<string>(StringComparer.Ordinal) { "ready", "decide", "hide", "size", "openVault" };
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> shownAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> displayedAt = new(StringComparer.Ordinal); // the last mark of each
    private readonly HashSet<string> dismissed = new(StringComparer.Ordinal);
    private DateTimeOffset? reportedAt;
    private bool opened;

    public int Count => vault.PendingApprovals().Count;

    public VaultApprovalsVisibility Sync()
    {
        var current = vault.PendingApprovals(); var now = clock.UtcNow;
        var ids = current.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var gone in shownAt.Keys.Where(id => !ids.Contains(id)).ToArray()) shownAt.Remove(gone);
            foreach (var gone in displayedAt.Where(m => !ids.Contains(m.Key) || !Recent(m.Value, now, DisplayedFor)).Select(m => m.Key).ToArray()) displayedAt.Remove(gone);
            dismissed.IntersectWith(ids);
            if (ids.Count == 0) opened = false;
            return new(opened || current.Any(a => !dismissed.Contains(a.Id) && Eligible(a, now)), ids.Count);
        }
    }
    // The user brought the pop-out up (tray menu or left click): it stays, whatever another app shows, until the
    // user closes it or none is left. False when none waits.
    public bool Open()
    {
        if (Count == 0) return false;
        lock (gate) opened = true;
        return true;
    }
    // The user closed it (its ×): hidden until an approval that is not pending now becomes eligible.
    public void Dismiss()
    {
        var ids = vault.PendingApprovals().Select(a => a.Id);
        lock (gate) { opened = false; dismissed.Clear(); dismissed.UnionWith(ids); }
    }

    // ── another app shows them ─────────────────────────────────────────────────
    // The ids the reporting app shows now, possibly none. Ids that are not pending (unknown or already decided)
    // are ignored. Any report counts as "an app that shows approvals is visible".
    public void Displayed(IEnumerable<string> ids)
    {
        var pending = vault.PendingApprovals().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var now = clock.UtcNow;
        lock (gate)
        {
            reportedAt = now;
            foreach (var id in ids) if (pending.Contains(id)) displayedAt[id] = now;
        }
    }
    public DateTimeOffset? ReportedAt { get { lock (gate) return reportedAt; } }
    public bool ShownElsewhere(string id)
    {
        var now = clock.UtcNow;
        lock (gate) return displayedAt.TryGetValue(id, out var at) && Recent(at, now, DisplayedFor);
    }
    // The body of POST /v1/vault/approvals/displayed, {"ids":[…]}: null unless it lists 0–50 approval ids.
    public static IReadOnlyList<string>? DisplayedIds(JsonObject body)
    {
        if (body["ids"] is not JsonArray list || list.Count > MaxDisplayed) return null;
        var ids = new List<string>(list.Count);
        foreach (var item in list)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var id) || !VaultProtocol.IsApprovalId(id)) return null;
            ids.Add(id);
        }
        return ids;
    }
    // Under the gate.
    private bool Eligible(VaultPendingApproval approval, DateTimeOffset now)
    {
        if (reportedAt is not { } reported || !Recent(reported, now, ReporterWindow)) return true; // no app reported lately: at once
        if (Recent(approval.ArrivedAt, now, DisplayGrace)) return false; // its grace
        return !(displayedAt.TryGetValue(approval.Id, out var marked) && Recent(marked, now, DisplayedFor)); // unless the app shows it
    }
    // [at, at + span): a wall clock set back keeps nothing alive for longer.
    private static bool Recent(DateTimeOffset at, DateTimeOffset now, TimeSpan span) => now >= at && now - at < span;

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
