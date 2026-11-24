namespace Construct.Agent;

/// <summary>Identifies one pending vault approval or entry request on an engine.</summary>
/// <param name="Kind"><c>approval</c> or <c>entry-request</c>.</param>
public sealed record DisplayedVaultItem(string Kind, string Engine, string Id)
{
    public static DisplayedVaultItem Approval(string engine, string id) => new(VaultDisplayTracker.KindApproval, engine, id);

    public static DisplayedVaultItem EntryRequest(string engine, string id) => new(VaultDisplayTracker.KindEntryRequest, engine, id);
}

/// <summary>
/// Body of <c>POST /api/vault/approvals/displayed</c>: the pending approvals and entry requests another app on this
/// device (such as T3 Code Desktop) shows inline right now. At most 50 items; an empty list is a heartbeat.
/// </summary>
public sealed record DisplayedVaultRequest(IReadOnlyList<DisplayedVaultItem> Items);

/// <summary>Outcome of evaluating the currently pending vault approvals and entry requests for the tray pop-out.</summary>
/// <param name="NewlyEligible">Items eligible to pop now that have not popped earlier in this run.</param>
/// <param name="Deferred">Items currently held back (waiting out display grace or displayed by another app) that have not popped yet.</param>
public sealed record VaultDisplayEvaluation(
    IReadOnlyList<DisplayedVaultItem> NewlyEligible,
    IReadOnlyList<DisplayedVaultItem> Deferred);

/// <summary>
/// Tracks which pending vault approvals and entry requests another app on this device (such as T3 Code Desktop)
/// currently shows inline, so the Construct tray pop-out steps back while that app is visible and showing them.
/// </summary>
/// <remarks>
/// Mirrors the old Companion's semantics:
/// <list type="bullet">
/// <item>While no app reported within <see cref="ReporterWindow"/> (10 s), a new item is eligible at once.</item>
/// <item>While a reporter is active, a new item waits <see cref="DisplayGrace"/> (5 s) from when it was first seen.</item>
/// <item>After the grace, an item marked displayed within <see cref="DisplayedFor"/> (8 s) is still held back; once
/// the reporter stops or leaves it out, the item becomes eligible on a subsequent poll.</item>
/// </list>
/// Only items that actually become eligible are marked as popped; suppressed items are never marked popped early.
/// </remarks>
public sealed class VaultDisplayTracker(TimeProvider? time = null)
{
    public static readonly TimeSpan ReporterWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DisplayGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DisplayedFor = TimeSpan.FromSeconds(8);
    public const int MaxDisplayed = 50;
    public const string KindApproval = "approval";
    public const string KindEntryRequest = "entry-request";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<DisplayedVaultItem, DateTimeOffset> _displayedAt = new();
    private readonly Dictionary<DisplayedVaultItem, DateTimeOffset> _firstSeen = new();
    private readonly HashSet<DisplayedVaultItem> _popped = new();
    private DateTimeOffset? _reportedAt;

    /// <summary>Records a displayed-items report (or an empty heartbeat) from another app on this device.</summary>
    public void Report(DisplayedVaultRequest? request)
    {
        var items = Validate(request);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _reportedAt = now;
            PruneExpiredMarksLocked(now);
            foreach (var item in items) _displayedAt[item] = now;
        }
    }

    /// <summary>Whether another app reported showing this item within <see cref="DisplayedFor"/>.</summary>
    public bool IsDisplayedElsewhere(string kind, string engine, string id)
    {
        var now = _time.GetUtcNow();
        var key = new DisplayedVaultItem(kind, engine, id);
        lock (_gate) return _displayedAt.TryGetValue(key, out var at) && Recent(at, now, DisplayedFor);
    }

    /// <summary>Whether another app sent a report (or heartbeat) within <see cref="ReporterWindow"/>.</summary>
    public bool HasActiveReporter()
    {
        var now = _time.GetUtcNow();
        lock (_gate) return _reportedAt is { } reported && Recent(reported, now, ReporterWindow);
    }

    /// <summary>
    /// Evaluates the currently pending approvals and entry requests and decides which are newly eligible to pop
    /// now and which remain deferred waiting on a reporter.
    /// </summary>
    public VaultDisplayEvaluation Evaluate(IEnumerable<DisplayedVaultItem> current, IEnumerable<string>? unreachableEngines = null)
    {
        var unique = new List<DisplayedVaultItem>();
        var present = new HashSet<DisplayedVaultItem>();
        foreach (var item in current)
        {
            if (present.Add(item)) unique.Add(item);
        }

        var unreachable = unreachableEngines is null ? null : new HashSet<string>(unreachableEngines, StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        var newlyEligible = new List<DisplayedVaultItem>();
        var deferred = new List<DisplayedVaultItem>();

        lock (_gate)
        {
            PruneExpiredMarksLocked(now);
            foreach (var gone in _firstSeen.Keys.Where(k => !present.Contains(k) && (unreachable is null || !unreachable.Contains(k.Engine))).ToArray())
                _firstSeen.Remove(gone);

            var reporterPresent = _reportedAt is { } reported && Recent(reported, now, ReporterWindow);
            foreach (var item in unique)
            {
                if (!_firstSeen.TryGetValue(item, out var arrived))
                    _firstSeen[item] = arrived = now;

                if (_popped.Contains(item)) continue;

                if (IsEligibleLocked(item, arrived, reporterPresent, now))
                {
                    _popped.Add(item);
                    newlyEligible.Add(item);
                }
                else
                {
                    deferred.Add(item);
                }
            }
        }

        return new VaultDisplayEvaluation(newlyEligible, deferred);
    }

    private bool IsEligibleLocked(DisplayedVaultItem item, DateTimeOffset arrived, bool reporterPresent, DateTimeOffset now)
    {
        if (!reporterPresent) return true;
        if (Recent(arrived, now, DisplayGrace)) return false;
        return !(_displayedAt.TryGetValue(item, out var marked) && Recent(marked, now, DisplayedFor));
    }

    private void PruneExpiredMarksLocked(DateTimeOffset now)
    {
        foreach (var expired in _displayedAt.Where(kv => !Recent(kv.Value, now, DisplayedFor)).Select(kv => kv.Key).ToArray())
            _displayedAt.Remove(expired);
    }

    // [at, at + span): a wall clock set back keeps nothing alive for longer.
    private static bool Recent(DateTimeOffset at, DateTimeOffset now, TimeSpan span) => now >= at && now - at < span;

    private static IReadOnlyList<DisplayedVaultItem> Validate(DisplayedVaultRequest? request)
    {
        if (request?.Items is null)
            throw new AgentFailure(400, "invalid-request", "items is required.");
        if (request.Items.Count > MaxDisplayed)
            throw new AgentFailure(400, "too-many-items", $"At most {MaxDisplayed} items may be reported.");
        foreach (var item in request.Items)
        {
            if (item is null)
                throw new AgentFailure(400, "invalid-item", "Each displayed item must specify kind, engine and id.");
            if (item.Kind is not (KindApproval or KindEntryRequest))
                throw new AgentFailure(400, "invalid-kind", "kind must be approval or entry-request.");
            if (!IsValidToken(item.Engine, 64, allowTilde: false))
                throw new AgentFailure(400, "invalid-engine", "Invalid engine id.");
            if (!IsValidToken(item.Id, 128, allowTilde: true))
                throw new AgentFailure(400, "invalid-id", "Invalid request id.");
        }

        return request.Items;
    }

    private static bool IsValidToken(string? value, int maxLength, bool allowTilde)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || value is "." or "..") return false;
        foreach (var ch in value)
        {
            var ok = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')
                || ch is '.' or '_' or '-' || (allowTilde && ch == '~');
            if (!ok) return false;
        }

        return true;
    }
}
