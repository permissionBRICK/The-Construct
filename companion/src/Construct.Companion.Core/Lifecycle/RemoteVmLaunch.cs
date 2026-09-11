using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Lifecycle;

public static class RemoteVmLaunch
{
    public static JsonArray ArgSpec(string url, string auth, string name, int cpu, int ram, int disk, bool supportsCpu, IReadOnlyList<string>? projects = null)
    {
        JsonObject Pair(string flag, string value) => new() { ["flag"] = flag, ["value"] = value };
        string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pairs = new JsonArray(Pair("-Backend", "hyperv-remote"), Pair("-ServiceUrl", url), Pair("-ServiceAuth", auth == "token" ? "token" : "negotiate"),
            Pair("-InstanceName", name), Pair("-VmMemoryGB", Number(ram)), Pair("-VmDiskGB", Number(disk)));
        if (supportsCpu) pairs.Add(Pair("-VmCpuCount", Number(cpu)));
        if (projects is not null) pairs.Add(Pair("-Projects", string.Join(',', projects)));
        return pairs;
    }
    public static string[] Arguments(JsonArray pairs) => pairs.SelectMany(p => new[] { StateJson.String(p!["flag"]), StateJson.String(p["value"]) }).ToArray();
}
