using Construct.Companion.Core.ConfigSync;
namespace Construct.Companion.Tests.ConfigSync;
public sealed class RulesTests
{
    [Fact] public void CanonicalNewlinesArePinnedOnWindowsToo() { Assert.Equal("\n", ConfigSyncRules.Json.NewLine); Assert.DoesNotContain('\r', ProfileCodec.CanonicalizeProfileText("a", "{\"name\":\"a\"}").Content!); }
    [Fact] public void MissingManifestUrlStillSkipsTracked() { var plan = ConfigSyncRules.PlanPublish([new("a", "{}")], new Dictionary<string,ManifestEntry> { ["a"] = new(null!, "", "", "") }, new Dictionary<string,string>()); Assert.Equal("already tracked -- use Push back", Assert.Single(plan.SkipTracked).Reason); }
    [Theory] [InlineData("../a")] [InlineData("default")] [InlineData("a:b")] public void StoreWriterRefusesUnsafeNames(string name) => Assert.Throws<ArgumentException>(() => StoreScripts.BuildWriteStoreScript([new(name,"write",null,"data")]));
}
