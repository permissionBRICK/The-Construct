using System.Text.Json.Nodes;

namespace Constructd.Sync.Profiles;

/// <summary>
/// The merged profile and the fields left in conflict. A conflicted field carries theirs (the
/// accepted store revision) in <see cref="Merged"/>; every other field carries the merge.
/// </summary>
public sealed record ProfileMergeResult(JsonObject Merged, IReadOnlyList<string> Conflicts)
{
    public bool Clean => Conflicts.Count == 0;
}

/// <summary>
/// The per-field profile merge of 9.6, a pure function over decoded JSON. <c>ours</c> is the write
/// that received the <c>412</c>, <c>theirs</c> the accepted store revision, <c>base</c> what ours was
/// based on (null when the writer has no base: then every field the two sides disagree on is a
/// conflict). Never a text merge.
/// </summary>
public static class ProfileMerge
{
    public static ProfileMergeResult Merge(JsonObject? baseProfile, JsonObject ours, JsonObject theirs)
    {
        var b = baseProfile is null ? null : ProfileRules.Normalize(baseProfile);
        var o = ProfileRules.Normalize(ours);
        var t = ProfileRules.Normalize(theirs);

        var merged = new JsonObject();
        var conflicts = new List<string>();
        var bothChanged = new HashSet<string>(StringComparer.Ordinal);

        var fields = o.Select(p => p.Key).Concat(t.Select(p => p.Key)).Concat(b?.Select(p => p.Key) ?? []).Distinct(StringComparer.Ordinal).ToList();
        foreach (var field in fields)
        {
            var bv = b?[field];
            var ov = o[field];
            var tv = t[field];
            if (Same(ov, tv)) { Set(merged, field, ov); continue; }
            if (b is not null && Same(ov, bv)) { Set(merged, field, tv); continue; }
            if (b is not null && Same(tv, bv)) { Set(merged, field, ov); continue; }

            bothChanged.Add(field);
            if (b is null) { conflicts.Add(field); Set(merged, field, tv); continue; }

            switch (field)
            {
                case "repos":
                    MergeList(field, bv, ov, tv, RepoKey, (x, y, z) => SameOrConflict(x, y, z), merged, conflicts);
                    break;
                case "mcp":
                    MergeList(field, bv, ov, tv, McpKey, MergeMcp, merged, conflicts);
                    break;
                case "forwards":
                    MergeList(field, bv, ov, tv, ForwardKey, MergeObject, merged, conflicts);
                    break;
                case "hostPackages":
                    MergeList(field, bv, ov, tv, n => n?.ToJsonString() ?? "null", (x, y, z) => SameOrConflict(x, y, z), merged, conflicts);
                    break;
                case "sdks":
                case "tests":
                    MergeKeyed(field, bv, ov, tv, merged, conflicts);
                    break;
                case "provisionCommands":
                    MergeCommands(field, bv, ov, tv, merged, conflicts);
                    break;
                default:
                    // `name` and any field added later: both changed differently.
                    conflicts.Add(field);
                    Set(merged, field, tv);
                    break;
            }
        }

        Check(merged, o, t, bothChanged, conflicts);
        return new ProfileMergeResult(ProfileRules.Finish(merged), conflicts.Distinct(StringComparer.Ordinal).ToList());
    }

    // ---- per-field rules ------------------------------------------------------------------------

    private static string RepoKey(JsonNode? repo) => repo is JsonObject r
        ? Key(r["url"]) + "\u0000" + Key(r["directory"])
        : Key(repo);

    private static string McpKey(JsonNode? item) => item is JsonObject m ? Key(m["name"]) : Key(item);

    private static string ForwardKey(JsonNode? item) => item is JsonObject f ? Key(f["port"]) + "\u0000" + Key(f["to"]) : Key(item);

    private static string Key(JsonNode? node) => node?.ToJsonString() ?? "null";

    /// <summary>Items whose key holds their whole content: a match is the same item.</summary>
    private static (JsonNode? Value, bool Conflict) SameOrConflict(JsonNode? b, JsonNode? o, JsonNode? t) =>
        Same(o, t) ? (o, false) : (t, true);

    /// <summary>An MCP server: field by field, but a change of its type on both sides is a conflict.</summary>
    private static (JsonNode? Value, bool Conflict) MergeMcp(JsonNode? b, JsonNode? o, JsonNode? t)
    {
        if (Same(o, t)) return (o, false);
        var type = b?["type"];
        if (b is not null && !Same(o?["type"], type) && !Same(t?["type"], type)) return (t, true);
        return MergeObject(b, o, t);
    }

    /// <summary>A matched object item, field by field; each value is one unit.</summary>
    private static (JsonNode? Value, bool Conflict) MergeObject(JsonNode? b, JsonNode? o, JsonNode? t)
    {
        if (Same(o, t)) return (o, false);
        if (o is not JsonObject oo || t is not JsonObject to) return (t, true);
        var bo = b as JsonObject;
        var result = new JsonObject();
        foreach (var key in oo.Select(p => p.Key).Concat(to.Select(p => p.Key)).Concat(bo?.Select(p => p.Key) ?? []).Distinct(StringComparer.Ordinal))
        {
            var bv = bo?[key];
            var ov = oo[key];
            var tv = to[key];
            JsonNode? pick;
            if (Same(ov, tv)) pick = ov;
            else if (bo is not null && Same(ov, bv)) pick = tv;
            else if (bo is not null && Same(tv, bv)) pick = ov;
            else return (t, true);
            Set(result, key, pick);
        }

        return (result, false);
    }

    /// <summary>
    /// <c>repos</c>, <c>mcp</c>, <c>forwards</c> and <c>hostPackages</c>: items match by key, then by
    /// occurrence among identical keys. Removal holds when the other side left the item alone;
    /// removal against a change is a conflict. Order: a side that alone reordered sets it and the
    /// other's additions follow; both reordering differently is a conflict; otherwise ours, then
    /// theirs' additions.
    /// </summary>
    private static void MergeList(
        string field,
        JsonNode? bNode,
        JsonNode? oNode,
        JsonNode? tNode,
        Func<JsonNode?, string> key,
        Func<JsonNode?, JsonNode?, JsonNode?, (JsonNode? Value, bool Conflict)> mergeItem,
        JsonObject merged,
        List<string> conflicts)
    {
        if (bNode is not JsonArray bl || oNode is not JsonArray ol || tNode is not JsonArray tl)
        {
            conflicts.Add(field);
            Set(merged, field, tNode);
            return;
        }

        var bItems = Index(bl, key);
        var oItems = Index(ol, key);
        var tItems = Index(tl, key);
        var bMap = bItems.ToDictionary(p => p.Key, p => p.Item);
        var oMap = oItems.ToDictionary(p => p.Key, p => p.Item);
        var tMap = tItems.ToDictionary(p => p.Key, p => p.Item);

        var kept = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var conflict = false;
        foreach (var k in oItems.Select(p => p.Key).Concat(tItems.Select(p => p.Key)).Concat(bItems.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
        {
            var inB = bMap.TryGetValue(k, out var bi);
            var inO = oMap.TryGetValue(k, out var oi);
            var inT = tMap.TryGetValue(k, out var ti);
            if (inO && inT)
            {
                var (value, itemConflict) = mergeItem(inB ? bi : null, oi, ti);
                conflict |= itemConflict;
                kept[k] = value;
            }
            else if (inO && !inB) kept[k] = oi;
            else if (inT && !inB) kept[k] = ti;
            else if (inO && inB) conflict |= !Same(oi, bi);
            else if (inT && inB) conflict |= !Same(ti, bi);
        }

        var bOrder = bItems.Select(p => p.Key).ToList();
        var oOrder = oItems.Select(p => p.Key).ToList();
        var tOrder = tItems.Select(p => p.Key).ToList();
        var oReordered = Reordered(bOrder, oOrder);
        var tReordered = Reordered(bOrder, tOrder);
        if (oReordered && tReordered)
        {
            var common = new HashSet<string>(bOrder.Where(k => oMap.ContainsKey(k) && tMap.ContainsKey(k)), StringComparer.Ordinal);
            if (!oOrder.Where(common.Contains).SequenceEqual(tOrder.Where(common.Contains))) conflict = true;
        }

        if (conflict)
        {
            conflicts.Add(field);
            Set(merged, field, tNode);
            return;
        }

        var (first, second) = tReordered && !oReordered ? (tOrder, oOrder) : (oOrder, tOrder);
        var order = first.Where(kept.ContainsKey).Concat(second.Where(k => kept.ContainsKey(k) && !first.Contains(k))).ToList();
        merged[field] = new JsonArray(order.Select(k => kept[k]?.DeepClone()).ToArray());
    }

    /// <summary><c>sdks</c> and <c>tests</c>: key by key, a value (string or list) is one unit.</summary>
    private static void MergeKeyed(string field, JsonNode? bNode, JsonNode? oNode, JsonNode? tNode, JsonObject merged, List<string> conflicts)
    {
        if (bNode is not JsonObject bo || oNode is not JsonObject oo || tNode is not JsonObject to)
        {
            conflicts.Add(field);
            Set(merged, field, tNode);
            return;
        }

        var result = new JsonObject();
        foreach (var key in oo.Select(p => p.Key).Concat(to.Select(p => p.Key)).Concat(bo.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
        {
            var bv = bo[key];
            var ov = oo[key];
            var tv = to[key];
            JsonNode? pick;
            if (Same(ov, tv)) pick = ov;
            else if (Same(ov, bv)) pick = tv;
            else if (Same(tv, bv)) pick = ov;
            else
            {
                conflicts.Add($"{field}.{key}");
                pick = tv;
            }

            Set(result, key, pick);
        }

        merged[field] = result;
    }

    /// <summary>
    /// <c>provisionCommands</c>: a three-way merge of the ordered list. Changes in different places
    /// both apply; insertions at the same place keep ours first, then theirs, an identical one once;
    /// the same item changed differently (or removed against a change) is a conflict.
    /// </summary>
    private static void MergeCommands(string field, JsonNode? bNode, JsonNode? oNode, JsonNode? tNode, JsonObject merged, List<string> conflicts)
    {
        if (bNode is not JsonArray bl || oNode is not JsonArray ol || tNode is not JsonArray tl)
        {
            conflicts.Add(field);
            Set(merged, field, tNode);
            return;
        }

        var result = Diff3(bl.Select(Key).ToList(), ol.Select(Key).ToList(), tl.Select(Key).ToList());
        if (result is null)
        {
            conflicts.Add(field);
            Set(merged, field, tNode);
            return;
        }

        merged[field] = new JsonArray(result.Select(k => JsonNode.Parse(k)).ToArray());
    }

    internal static List<string>? Diff3(List<string> b, List<string> o, List<string> t)
    {
        var mo = Lcs(b, o);
        var mt = Lcs(b, t);
        var result = new List<string>();
        int bi = 0, oi = 0, ti = 0;
        while (true)
        {
            if (bi < b.Count && mo[bi] == oi && mt[bi] == ti)
            {
                result.Add(b[bi]);
                bi++; oi++; ti++;
                continue;
            }

            // The next base item both sides kept: everything before it is one changed chunk.
            var next = bi;
            while (next < b.Count && (mo[next] < 0 || mt[next] < 0)) next++;
            var (oNext, tNext) = next < b.Count ? (mo[next], mt[next]) : (o.Count, t.Count);
            var chunk = ResolveChunk(b[bi..next], o[oi..oNext], t[ti..tNext]);
            if (chunk is null) return null;
            result.AddRange(chunk);
            (bi, oi, ti) = (next, oNext, tNext);
            if (bi >= b.Count) return result;
        }
    }

    private static List<string>? ResolveChunk(List<string> b, List<string> o, List<string> t)
    {
        if (o.SequenceEqual(b)) return t;
        if (t.SequenceEqual(b)) return o;
        if (o.SequenceEqual(t)) return o;
        if (b.Count == 0) return Insertions(o, t);

        // A side that kept the base items in one piece only inserted around them.
        var oAround = Around(o, b);
        var tAround = Around(t, b);
        if (oAround is { } oa && tAround is { } ta) return [.. Insertions(oa.Before, ta.Before), .. b, .. Insertions(oa.After, ta.After)];
        if (tAround is { } tOnly) return [.. tOnly.Before, .. o, .. tOnly.After];
        if (oAround is { } oOnly) return [.. oOnly.Before, .. t, .. oOnly.After];
        return null;
    }

    /// <summary>Both sides inserted here: ours first, then theirs, an identical insertion once.</summary>
    private static List<string> Insertions(List<string> o, List<string> t)
    {
        var left = o.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>(o);
        foreach (var item in t)
        {
            if (left.TryGetValue(item, out var count) && count > 0) left[item] = count - 1;
            else result.Add(item);
        }

        return result;
    }

    private static (List<string> Before, List<string> After)? Around(List<string> side, List<string> b)
    {
        for (var start = 0; start + b.Count <= side.Count; start++)
        {
            if (side.Skip(start).Take(b.Count).SequenceEqual(b, StringComparer.Ordinal))
                return (side[..start], side[(start + b.Count)..]);
        }

        return null;
    }

    /// <summary>For each base index, the index it matches in <paramref name="other"/> (longest common subsequence), or -1.</summary>
    private static int[] Lcs(List<string> b, List<string> other)
    {
        var n = b.Count;
        var m = other.Count;
        var len = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                len[i, j] = b[i] == other[j] ? len[i + 1, j + 1] + 1 : Math.Max(len[i + 1, j], len[i, j + 1]);

        var map = Enumerable.Repeat(-1, n).ToArray();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (b[x] == other[y]) { map[x] = y; x++; y++; }
            else if (len[x + 1, y] >= len[x, y + 1]) x++;
            else y++;
        }

        return map;
    }

    // ---- checks after the merge -----------------------------------------------------------------

    private static void Check(JsonObject merged, JsonObject o, JsonObject t, HashSet<string> bothChanged, List<string> conflicts)
    {
        void Fail(string field)
        {
            conflicts.Add(field);
            Set(merged, field, t[field]);
        }

        // The first repository sets the working directory of the commands.
        if (bothChanged.Contains("repos") && !conflicts.Contains("repos"))
        {
            var first = (merged["repos"] as JsonArray)?.FirstOrDefault();
            if (!Same(first, (o["repos"] as JsonArray)?.FirstOrDefault()) || !Same(first, (t["repos"] as JsonArray)?.FirstOrDefault())) Fail("repos");
        }

        if (!conflicts.Contains("repos") && Duplicated(merged["repos"], o["repos"], t["repos"], ProfileRules.EffectiveDirectory)) Fail("repos");
        if (!conflicts.Contains("forwards") && Duplicated(merged["forwards"], o["forwards"], t["forwards"], ForwardKey)) Fail("forwards");
        if (!conflicts.Contains("mcp") && Duplicated(merged["mcp"], o["mcp"], t["mcp"], McpKey)) Fail("mcp");

        // Schema and the write-path checks; a failing field becomes a conflict. Theirs passed them
        // when it was written, so falling back to it settles the field.
        foreach (var field in ProfileRules.Validate(ProfileRules.Finish(merged)).Select(p => p.Field).Distinct(StringComparer.Ordinal).ToList())
        {
            if (!conflicts.Contains(field)) Fail(field);
        }
    }

    /// <summary>A key occurs in the merge more often than either side already had it.</summary>
    private static bool Duplicated(JsonNode? merged, JsonNode? o, JsonNode? t, Func<JsonNode?, string> key)
    {
        static Dictionary<string, int> Count(JsonNode? list, Func<JsonNode?, string> key) =>
            (list as JsonArray ?? []).GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var ours = Count(o, key);
        var theirs = Count(t, key);
        return Count(merged, key).Any(p => p.Value > 1 && p.Value > Math.Max(ours.GetValueOrDefault(p.Key), theirs.GetValueOrDefault(p.Key)));
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static List<(string Key, JsonNode? Item)> Index(JsonArray list, Func<JsonNode?, string> key)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<(string, JsonNode?)>();
        foreach (var item in list)
        {
            var k = key(item);
            var n = seen.GetValueOrDefault(k);
            seen[k] = n + 1;
            result.Add(($"{k}\u0001{n}", item));
        }

        return result;
    }

    /// <summary>The side changed the relative order of the base items it kept.</summary>
    private static bool Reordered(List<string> bOrder, List<string> side)
    {
        var kept = new HashSet<string>(side, StringComparer.Ordinal);
        var inBase = new HashSet<string>(bOrder, StringComparer.Ordinal);
        return !bOrder.Where(kept.Contains).SequenceEqual(side.Where(inBase.Contains));
    }

    internal static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static void Set(JsonObject target, string field, JsonNode? value)
    {
        if (value is null) target.Remove(field);
        else target[field] = value.DeepClone();
    }
}
