using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.ConfigSync;

public static class ConfigSyncRules
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Serialize<T>(T value) => NormalizeSerialized(JsonSerializer.Serialize(value, Json)) + "\n";
    private static string NormalizeSerialized(string json)
    {
        // System.Text.Json escapes a few printable JS string code points even with
        // UnsafeRelaxedJsonEscaping. Unescape only complete JSON string tokens.
        return Regex.Replace(json, "\"(?:[^\"\\\\]|\\\\.)*\"", match =>
        {
            var text = match.Value;
            return Regex.Replace(text, @"(?<!\\)(?:\\\\)*\\u([0-9a-fA-F]{4})", escape =>
            {
                var code = Convert.ToInt32(escape.Groups[1].Value, 16);
                return code >= 0x7f && !char.IsSurrogate((char)code) ? escape.Value[..^6] + (char)code : code < 0x20 ? escape.Value[..^6] + "\\u" + code.ToString("x4", System.Globalization.CultureInfo.InvariantCulture) : escape.Value;
            });
        });
    }
    internal static IEnumerable<string> JsKeys(IEnumerable<string> keys) => keys.Select((key,index) => (key,index,number: uint.TryParse(key,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var n) && n < uint.MaxValue && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == key ? (long)n : -1)).OrderBy(p=>p.number < 0 ? 1 : 0).ThenBy(p=>p.number < 0 ? p.index : p.number).Select(p=>p.key);
    public static bool IsReserved(string name) => new[] { "default", "project.schema" }.Contains(name.Trim().ToLowerInvariant());
    public static bool IsSafeProfileName(string name) => name.Length > 0 && HostState.SafeProfileName(name) == name;
    public static bool IsValidVmBranch(string? name) => Instances.IsValidConfigBranch(name);
    public static string ResolveVmBranch(string? name, Action<string>? warn = null)
    {
        if (string.IsNullOrEmpty(name)) return "vm";
        if (IsValidVmBranch(name)) return name;
        warn?.Invoke($"invalid config-sync branch name \"{RedactGitOutput(name)}\"; falling back to \"vm\""); return "vm";
    }
    public static bool IsValidPublishBranch(string? name) => !string.IsNullOrEmpty(name) && Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9._/-]*$") && !name.Contains("..", StringComparison.Ordinal) && !name.Contains("//", StringComparison.Ordinal) && !name.EndsWith('.') && !name.EndsWith('/') && !name.EndsWith(".lock", StringComparison.Ordinal);
    public static string RedactGitOutput(string? value) => Regex.Replace(value ?? "", @"(?<=://)[^/@\s]+(?=@)", "***");
    public static string DisplayRemoteUrl(string? value) => RedactGitOutput(value);
    public static bool UrlHasCredentials(string? url)
    {
        var m = Regex.Match((url ?? "").Trim(), @"^([A-Za-z][A-Za-z0-9+.-]*)://([^/@\s]+)@");
        if (!m.Success) return false;
        var user = m.Groups[2].Value;
        if (user.Contains(':')) return true;
        if (Regex.IsMatch(user, @"^gitgud-project(\.|$)")) return false;
        return (user.Length >= 32 && !user.Contains('.')) || Regex.IsMatch(user, "^(ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|glpat-|glptt-|gldt-|glrt-|ggpat_|ggpt_|ggjt_|oauth2$|x-access-token$)");
    }
    public static string? ValidateConfigRemoteUrl(string? value)
    {
        var s = (value ?? "").Trim();
        if (!(Regex.IsMatch(s, @"^(https?|ssh|git)://[^\s]+$", RegexOptions.IgnoreCase) || Regex.IsMatch(s, @"^[^@\s]+@[^:\s]+:[^\s]+$"))) return "Enter an https://, ssh:// or git@host:path git URL.";
        return UrlHasCredentials(s) ? "Remove the credentials from the URL -- let your git credential helper supply them." : null;
    }
    public static string RemoteSlug(string url) => Regex.Replace(url, "[^A-Za-z0-9._-]", "-");
    public static string ResolveRemoteUrl(IEnumerable<ConfigRemote> remotes, string wanted) => remotes.FirstOrDefault(r => r.Url == wanted)?.Url ?? remotes.FirstOrDefault(r => DisplayRemoteUrl(r.Url) == wanted)?.Url ?? "";
    public static ManifestEntry PublishManifestEntry(string remoteUrl, string @ref, string name, string baseCommit, string baseBlobSha) => new(remoteUrl, @ref, "projects/" + name + ".json", name, baseCommit, baseBlobSha);
    public static IReadOnlyList<WriteOperation> PlanWriteBack(IReadOnlyDictionary<string, string> main, IReadOnlyDictionary<string, string> vm)
    {
        var result = new List<WriteOperation>();
        foreach (var name in JsKeys(main.Keys).Concat(JsKeys(vm.Keys)).Distinct())
        {
            var onMain = main.TryGetValue(name, out var content); var onVm = vm.TryGetValue(name, out var expect);
            if (onMain && (!onVm || content != expect)) result.Add(new(name, "write", expect, content));
            else if (!onMain && onVm) result.Add(new(name, "delete", expect));
        }
        return result;
    }
    public static ImportPlan PlanUpstreamImport(IEnumerable<ImportSelection> selected, IReadOnlyDictionary<string, ManifestEntry> manifest, IEnumerable<string> existingNames)
    {
        var result = new ImportPlan(); var existing = existingNames.ToHashSet();
        foreach (var s in selected)
        {
            var entry = new ManifestEntry(s.RemoteUrl, s.Ref, s.RelPath, s.Name);
            if (manifest.TryGetValue(s.Name, out var old) && old.RemoteUrl == s.RemoteUrl && old.PathInRemote == s.RelPath) result.Updates.Add(new(s.Name, null, s.Content, entry));
            else if (existing.Contains(s.Name))
            {
                var n = 2; while (existing.Contains(s.Name + "-" + n)) n++;
                result.Collisions.Add(new(s.Name, s.Name + "-" + n));
            }
            else { result.Creates.Add(new(s.Name, s.Content, entry)); existing.Add(s.Name); }
        }
        return result;
    }
    public static PublishPlan PlanPublish(IEnumerable<ProfileInput> profiles, IReadOnlyDictionary<string, ManifestEntry> manifest, IReadOnlyDictionary<string, string> remoteFiles, IEnumerable<string>? selected = null)
    {
        var result = new PublishPlan(); var want = selected?.ToHashSet();
        var lower = new Dictionary<string, string>(); foreach (var key in remoteFiles.Keys) lower[key.ToLowerInvariant()] = key;
        void Reject(List<ProfileReason> list, string name, string reason) { reason = RedactGitOutput(reason); list.Add(new(name, reason)); result.Reasons[name] = reason; }
        foreach (var p in profiles)
        {
            var name = p.Name;
            if (name.Length == 0 || IsReserved(name) || name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) || (want != null && !want.Contains(name))) continue;
            if (!IsSafeProfileName(name)) { Reject(result.Invalid, name, "is not a safe profile file name"); continue; }
            if (manifest.TryGetValue(name, out var entry)) { Reject(result.SkipTracked, name, "already tracked" + (!string.IsNullOrEmpty(entry.RemoteUrl) ? " by " + DisplayRemoteUrl(entry.RemoteUrl) : "") + " -- use Push back"); continue; }
            var gate = ProfileCodec.CanonicalizeProfileText(name, p.Raw);
            if (!gate.Ok) { Reject(result.Invalid, name, gate.Reason!); continue; }
            var upstreamKey = lower.GetValueOrDefault(name.ToLowerInvariant());
            if (upstreamKey != null && upstreamKey != name) { Reject(result.Refuse, name, $"the remote already has projects/{upstreamKey}.json, which is the SAME file as projects/{name}.json on Windows -- rename one of them, or import it first"); continue; }
            var upstream = upstreamKey == null ? null : remoteFiles[upstreamKey];
            if (upstream != null && (ProfileCodec.CanonicalizeProfileText(name, upstream).Content ?? upstream) != gate.Content) { Reject(result.Refuse, name, $"the remote already has projects/{name}.json with different content -- import it first, then push back"); continue; }
            result.Publish.Add(new(name, "projects/" + name + ".json", gate.Content!, upstream != null));
        }
        return result;
    }
    public static IReadOnlyList<PublishPickerItem> BuildPublishPickerItems(PublishPlan plan)
    {
        var items = new List<PublishPickerItem>();
        if (plan.Publish.Count > 0) { items.Add(new("publish", Kind: "separator")); items.AddRange(plan.Publish.Select(p => new PublishPickerItem(p.Name, p.Adopt ? "already upstream, identical -- adopt only" : "", Picked: true, Blocked: false))); }
        if (plan.SkipTracked.Count > 0) { items.Add(new("already tracked -- use Push back", Kind: "separator")); items.AddRange(plan.SkipTracked.Select(p => new PublishPickerItem(p.Name, p.Reason, Picked: false, Blocked: true))); }
        var bad = plan.Refuse.Concat(plan.Invalid).ToArray();
        if (bad.Length > 0) { items.Add(new("cannot be published", Kind: "separator")); items.AddRange(bad.Select(p => new PublishPickerItem(p.Name, p.Reason, Picked: false, Blocked: true))); }
        return items;
    }
    public static IEnumerable<PublishPickerItem> FilterPublishSelection(IEnumerable<PublishPickerItem> items) => items.Where(i => i.Blocked != true && i.Kind != "separator");
}
public sealed record ConfigRemote(string Url) { public override string ToString() => ConfigSyncRules.DisplayRemoteUrl(Url); }
public sealed record ManifestEntry(string RemoteUrl, string Ref, string PathInRemote, string ImportedAs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseCommit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseBlobSha = null)
{ public override string ToString() => "ManifestEntry"; }
public sealed record WriteOperation(string Name, string Action, string? Expect, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Content = null) { public override string ToString() => "WriteOperation"; }
public sealed record ImportSelection(string RemoteUrl, string Ref, string RelPath, string Name, string Content) { public override string ToString() => "ImportSelection"; }
public sealed record ImportCreate(string Name, string Content, ManifestEntry ManifestEntry) { public override string ToString() => "ImportCreate"; }
public sealed record ImportUpdate(string Name, string? BaseContent, string TheirsContent, ManifestEntry ManifestEntry) { public override string ToString() => "ImportUpdate"; }
public sealed record ImportCollision(string Name, string Suggested);
public sealed class ImportPlan { public List<ImportCreate> Creates { get; } = []; public List<ImportUpdate> Updates { get; } = []; public List<ImportCollision> Collisions { get; } = []; }
public sealed record ProfileInput(string Name, string Raw) { public override string ToString() => "ProfileInput"; }
public sealed record ProfileReason(string Name, string Reason);
public sealed record PublishFile(string Name, string PathInRemote, string Content, bool Adopt) { public override string ToString() => "PublishFile"; }
public sealed class PublishPlan { public List<PublishFile> Publish { get; } = []; public List<ProfileReason> SkipTracked { get; } = []; public List<ProfileReason> Refuse { get; } = []; public List<ProfileReason> Invalid { get; } = []; public Dictionary<string, string> Reasons { get; } = []; }
public sealed record PublishPickerItem(string Label,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Kind = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Picked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Blocked = null);
