using System.Text.Json.Nodes;
namespace Construct.Companion.Core.State;

public static class ProjectImport
{
    public static string ScanScript(string root = "/root/repos") => GuestScripts.Render("project-scan", new Dictionary<string, string> { ["root"] = root.Replace("'", "'\\''", StringComparison.Ordinal) });
    public static JsonArray? ParseScan(string stdout)
    {
        var repos = new JsonArray(); var ended = false;
        foreach (var line in stdout.Split('\n'))
        {
            if (line == "END") { ended = true; continue; }
            var parts = line.Split('\t');
            if (parts.Length >= 3 && parts[0].Trim().Length > 0) repos.Add(new JsonObject { ["name"] = parts[0].Trim(), ["url"] = parts[1].Trim(), ["branch"] = parts[2].Trim() });
        }
        return ended ? repos : null;
    }
    public static JsonObject Plan(JsonArray scan, JsonObject existing, IEnumerable<string>? ignoredNames = null, IEnumerable<string>? ignoredUrls = null)
    {
        var names = existing.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var covered = existing.SelectMany(p => (p.Value?["repos"] as JsonArray ?? []).OfType<JsonObject>()).Select(p => StateJson.Text(p["url"])?.Trim()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var deletedNames = (ignoredNames ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase); var deletedUrls = (ignoredUrls ?? []).ToHashSet(StringComparer.Ordinal);
        var writes = new JsonArray(); var skipped = new JsonArray(); var coveredNames = new JsonArray();
        foreach (var repo in scan.OfType<JsonObject>())
        {
            var name = StateJson.String(repo["name"]); var url = StateJson.String(repo["url"]).Trim();
            if (name.Length == 0) continue;
            if (url.Length == 0 || deletedNames.Contains(name) || deletedUrls.Contains(url)) { skipped.Add(name); continue; }
            if (covered.Contains(url) || names.Contains(name)) { coveredNames.Add(name); continue; }
            covered.Add(url); names.Add(name);
            writes.Add(new JsonObject { ["name"] = name, ["profile"] = new JsonObject { ["name"] = name,
                ["repos"] = new JsonArray(new JsonObject { ["url"] = url, ["directory"] = name }), ["sdks"] = new JsonObject(), ["mcp"] = new JsonArray(),
                ["hostPackages"] = new JsonArray(), ["provisionCommands"] = new JsonArray(), ["tests"] = new JsonObject() } });
        }
        return new() { ["toWrite"] = writes, ["skipped"] = skipped, ["covered"] = coveredNames };
    }
}
