using System.Text.Json.Nodes;
namespace Construct.Companion.Core.State;
public static class ProjectNavigation
{
    public static string OpenPath(JsonNode? profile)
    {
        if (profile?["repos"] is not JsonArray repos || repos.Count != 1 || repos[0] is not JsonObject repo) return "/root/repos";
        var directory = repo["directory"] is null ? "" : StateJson.String(repo["directory"]);
        if (directory.Length == 0)
        {
            var url = (repo["url"] is null ? "" : StateJson.String(repo["url"])).TrimEnd('/'); directory = url[(url.LastIndexOf('/') + 1)..];
            if (directory.EndsWith(".git", StringComparison.Ordinal)) directory = directory[..^4];
        }
        return directory.Length > 0 && directory.Split(['/', '\\']).All(s => s is not ("" or "." or "..")) ? "/root/repos/" + directory : "/root/repos";
    }
}
