using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Tests.Parity;
public sealed class ConfigSyncParityTests
{
    public static IEnumerable<object[]> Rows => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "Fixtures", "config-sync.json")))!.AsArray().Select((row, i) => new object[] { i, row!.ToJsonString() });
    private static T Read<T>(JsonNode? node) => node!.Deserialize<T>(ConfigSyncRules.Json)!;
    [Theory, MemberData(nameof(Rows))]
    public void MatchesJavaScript(int index, string raw)
    {
        _ = index; var row = JsonNode.Parse(raw)!; var input = row["input"]; var kind = row["kind"]!.GetValue<string>();
        var text = input is JsonValue v && v.TryGetValue<string>(out var value) ? value : "";
        object? result = kind switch
        {
            "vmBranch" => ConfigSyncRules.IsValidVmBranch(text), "publishBranch" => ConfigSyncRules.IsValidPublishBranch(text), "safeName" => ConfigSyncRules.IsSafeProfileName(text),
            "credentials" => ConfigSyncRules.UrlHasCredentials(text), "url" => ConfigSyncRules.ValidateConfigRemoteUrl(text), "redact" => ConfigSyncRules.RedactGitOutput(text), "slug" => ConfigSyncRules.RemoteSlug(text),
            "writeBack" => ConfigSyncRules.PlanWriteBack(Read<Dictionary<string,string>>(input!["mainFiles"]), Read<Dictionary<string,string>>(input["vmFiles"])),
            "readScript" => StoreScripts.BuildReadStoreScript(text), "writeScript" => StoreScripts.BuildWriteStoreScript(Read<WriteOperation[]>(input!["ops"]), input["root"]!.GetValue<string>()),
            "readResult" => StoreScripts.ParseReadStore(text), "writeResult" => StoreScripts.ParseWriteResult(text),
            "canonical" => ProfileCodec.CanonicalizeProfileText(input!["name"]!.GetValue<string>(), input["raw"]!.GetValue<string>()),
            "import" => ConfigSyncRules.PlanUpstreamImport(Read<ImportSelection[]>(input!["selected"]), Read<Dictionary<string,ManifestEntry>>(input["manifest"]), Read<string[]>(input["existingNames"])),
            "publish" => ConfigSyncRules.PlanPublish(Read<ProfileInput[]>(input!["profiles"]), Read<Dictionary<string,ManifestEntry>>(input["manifest"]), Read<Dictionary<string,string>>(input["remoteFiles"]), input["selected"] == null ? null : Read<string[]>(input["selected"])),
            "filterPicker" => ConfigSyncRules.FilterPublishSelection(Read<PublishPickerItem[]>(input)),
            "resolveRemote" => ConfigSyncRules.ResolveRemoteUrl(Read<ConfigRemote[]>(input!["remotes"]),input["wanted"]!.GetValue<string>()),
            "resolveBranch" => ResolveBranch(text),
            "share" => ConfigSharing.BuildShareCommand(input!["configRepoUrl"]!.GetValue<string>(),Read<string[]>(input["names"]),input["installRepo"]!.GetValue<string>(),input["installRef"]!.GetValue<string>()),
            "deploy" => ConfigSharing.BuildDeployPs1(input!["installRepo"]!.GetValue<string>(),input["installRef"]!.GetValue<string>()),
            "picker" => ConfigSyncRules.BuildPublishPickerItems(ReadPlan(input!)),
            "manifest" => ConfigSyncRules.Serialize(ConfigSyncRules.PublishManifestEntry(input!["remoteUrl"]!.GetValue<string>(), input["ref"]!.GetValue<string>(), input["name"]!.GetValue<string>(), input["baseCommit"]!.GetValue<string>(), input["baseBlobSha"]!.GetValue<string>())),
            _ => throw new InvalidOperationException(kind)
        };
        Assert.True(JsonNode.DeepEquals(row["output"], JsonSerializer.SerializeToNode(result, ConfigSyncRules.Json)), $"JavaScript parity failed: {kind}, row {index}");
    }
    private static object ResolveBranch(string name) { var warnings=new List<string>(); var branch=ConfigSyncRules.ResolveVmBranch(name,warnings.Add); return new {branch,warnings}; }
    private static PublishPlan ReadPlan(JsonNode input)
    {
        var plan = new PublishPlan(); plan.Publish.AddRange(Read<PublishFile[]>(input["publish"])); plan.SkipTracked.AddRange(Read<ProfileReason[]>(input["skipTracked"])); plan.Refuse.AddRange(Read<ProfileReason[]>(input["refuse"])); plan.Invalid.AddRange(Read<ProfileReason[]>(input["invalid"])); return plan;
    }
}
