using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Drivers;

// vmpower.planResourceApply: form values and the last measured guest size.
public static class VmResourcePlan
{
    public static double? Number(JsonNode? value, bool integer)
    {
        if (value is null || StateJson.Boolean(value) is not null || StateJson.String(value).Trim().Length == 0) return null;
        var number = Core.Probe.ProbeParser.JsNumber(StateJson.String(value));
        return double.IsFinite(number) && number > 0 && (!integer || Math.Floor(number) == number) ? number : null;
    }
    public static JsonObject Create(JsonObject? saved, JsonObject? live)
    {
        var ram = Number(saved?["ram"], false); var cpu = Number(saved?["cpu"], true);
        var liveRam = Number(live?["ramGb"], false); var liveCpu = Number(live?["cpus"], true);
        var checks = new List<bool?>();
        if (ram is not null) checks.Add(liveRam is null ? null : ram != liveRam);
        if (cpu is not null) checks.Add(liveCpu is null ? null : cpu != liveCpu);
        bool? pending = checks.Any(c => c == true) ? true : checks.Count > 0 && checks.All(c => c == false) ? false : null;
        return new() { ["ram"] = ram, ["cpu"] = cpu, ["none"] = checks.Count == 0, ["pending"] = pending,
            ["summary"] = Format(ram, cpu), ["current"] = Format(liveRam, liveCpu) };
    }
    private static string Format(double? ram, double? cpu) => string.Join(", ", new[] {
        ram is null ? null : StateJson.String(JsonValue.Create(ram)) + " GB RAM",
        cpu is null ? null : StateJson.String(JsonValue.Create(cpu)) + (cpu == 1 ? " vCPU" : " vCPUs") }.OfType<string>());
}
