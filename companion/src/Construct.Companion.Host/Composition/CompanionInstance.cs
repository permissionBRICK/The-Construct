using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Composition;

// The mutable per-instance state shared by dispatch, enrichment and the runtime supervisor.
// Serial guards commands; EnrichmentSerial guards the slower refresh so a refresh never blocks a command.
public sealed class CompanionInstance(JsonObject definition, InstanceStateStore store, ISshTransport ssh)
{
    public JsonObject Definition { get; set; } = definition;
    public string Name => StateJson.String(Definition["name"]);
    public InstanceStateStore Store { get; set; } = store;
    public ISshTransport Ssh { get; set; } = ssh;
    public SemaphoreSlim Serial { get; } = new(1, 1);
    public InstanceRuntime? Runtime { get; set; }
    public ConfigSyncArea? ConfigSync { get; set; }
    public SemaphoreSlim EnrichmentSerial { get; } = new(1, 1);
    public Dictionary<string, (DateTimeOffset At, string? Raw)> UsageCache { get; } = new();
    public string UsagePeriod { get; set; } = "daily";
    public JsonObject? Usage { get; set; }
    public string? UsageRaw { get; set; }
    public JsonObject Enrichment { get; set; } = new();
    public JsonNode? ConfigState { get; set; }
}
