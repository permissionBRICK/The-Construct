using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// The pure rules of the hosted vault: the sync merge rule and the texts the approval prompts show.
/// The texts are the Companion's (VaultService: <c>Bullet</c>, <c>Footer</c>, <c>Access</c>), except that
/// access is a duration ("for 2 h") rather than a clock time: the host's time zone is not the user's.
/// </summary>
public static class VaultRules
{
    /// <summary>Tombstones are kept this long, so every PC and host learns about a deletion.</summary>
    public static readonly TimeSpan TombstoneAge = TimeSpan.FromDays(30);

    /// <summary>
    /// The merge rule of every side: per name, the larger <c>updatedAt</c> wins; on a tie the lexically
    /// larger <c>updatedBy</c> (ordinal). An identical stamp is no change.
    /// </summary>
    public static bool Wins(VaultEntry candidate, VaultEntry? current)
    {
        if (current is null) return true;
        if (candidate.UpdatedAt != current.UpdatedAt) return candidate.UpdatedAt > current.UpdatedAt;
        return string.CompareOrdinal(candidate.UpdatedBy, current.UpdatedBy) > 0;
    }

    /// <summary>
    /// Applies the rule to a whole incoming set: the winners to write, in input order. An expired
    /// tombstone that would only be stored to be pruned is skipped.
    /// </summary>
    public static IReadOnlyList<VaultEntry> Merge(IReadOnlyList<VaultEntry> current, IEnumerable<VaultEntry> incoming, long nowMs)
    {
        var byName = current.ToDictionary(e => e.Name, StringComparer.Ordinal);
        var cutoff = nowMs - (long)TombstoneAge.TotalMilliseconds;
        var winners = new Dictionary<string, VaultEntry>(StringComparer.Ordinal);
        foreach (var entry in incoming)
        {
            if (entry.Deleted && entry.UpdatedAt < cutoff && !byName.ContainsKey(entry.Name)) continue;
            var against = winners.TryGetValue(entry.Name, out var pending) ? pending : byName.GetValueOrDefault(entry.Name);
            if (Wins(entry, against)) winners[entry.Name] = entry;
        }
        return winners.Values.ToArray();
    }

    public static string Bullet(VaultEntry entry) =>
        $"  • {entry.Name}{(entry.Description.Length > 0 ? " — " + entry.Description : "")}{(entry.HasUsername ? " (with username)" : "")}";

    public static string Footer(VaultRequest request) =>
        (request.Reason.Length > 0 ? $"\nReason given: “{request.Reason}”" : "") + (request.Source.Length > 0 ? $"\nRequested by {request.Source}" : "");

    /// <summary>"3 uses, for 2 h 30 min" — the access a lease grants, as uses and a duration.</summary>
    public static string Access(int? uses, TimeSpan span) =>
        (uses is { } u ? $"{u} use{(u == 1 ? "" : "s")}, " : "") + "for " + Duration(span);

    public static string Duration(TimeSpan left) => left >= TimeSpan.FromDays(1) ? "1 day" : left >= TimeSpan.FromHours(1)
        ? $"{(int)left.TotalHours} h{(left.Minutes > 0 ? $" {left.Minutes} min" : "")}" : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min";

    public static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}

/// <summary>What an approval prompt shows, rendered by the host (the Companion and the phone show it as is).</summary>
public sealed record VaultPrompt(string Title, string Message, string Action);

/// <summary>The approval texts, one per operation that needs the user's answer.</summary>
public static class VaultPrompts
{
    public static VaultPrompt Request(string vm, IReadOnlyList<VaultEntry> entries, VaultRequest request, TimeSpan ttl) =>
        new("Key vault — access request",
            $"The VM “{vm}” asks for access to:\n{string.Join('\n', entries.Select(VaultRules.Bullet))}\n\nAccess: {VaultRules.Access(request.Uses, ttl)}"
            + VaultRules.Footer(request), "Approve");

    public static VaultPrompt Once(string vm, VaultEntry entry, VaultRequest request) =>
        new("Key vault — one-time access", $"The VM “{vm}” asks to read this secret once:\n{VaultRules.Bullet(entry)}" + VaultRules.Footer(request), "Allow once");

    public static VaultPrompt Replace(string vm, VaultEntry entry, VaultRequest request) =>
        new("Key vault — replace secret",
            $"The VM “{vm}” wants to replace the value of:\n{VaultRules.Bullet(entry)}\n\nNew description: {request.Description}" + VaultRules.Footer(request), "Replace");

    public static VaultPrompt Delete(string vm, VaultEntry entry, VaultRequest request) =>
        new("Key vault — delete secret", $"The VM “{vm}” asks to delete this secret from the key vault:\n{VaultRules.Bullet(entry)}" + VaultRules.Footer(request), "Delete");
}
