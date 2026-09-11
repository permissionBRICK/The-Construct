using System.Text.Json.Nodes;
using Construct.Companion.Core.ConfigSync;
namespace Construct.Companion.Host.ConfigSync;
public sealed partial class ConfigRepository
{
    public async Task<DeletedProfiles> DeletedProfileIdentitiesAsync(IEnumerable<string> currentNames,CancellationToken ct=default)
    {
        var current=currentNames.ToHashSet(StringComparer.OrdinalIgnoreCase); var paths=new HashSet<string>(); var names=new HashSet<string>(); var urls=new HashSet<string>();
        foreach(var args in new[]{new[]{"diff","--name-only","--diff-filter=D","--","projects/"},new[]{"log","--all","--name-only","--pretty=format:","--diff-filter=D","--","projects/"}})
        {
            var result=await git.RunAsync(Directory,args,cancellationToken:ct);
            foreach(var path in result.Stdout.Split('\n').Select(s=>s.Trim().Replace('\\','/'))) if(System.Text.RegularExpressions.Regex.IsMatch(path,@"^projects/[^/]+\.json$")) paths.Add(path);
        }
        foreach(var path in paths)
        {
            var name=Path.GetFileNameWithoutExtension(path); if(current.Contains(name)||ConfigSyncRules.IsReserved(name)) continue; names.Add(name);
            var commits=await git.RunAsync(Directory,["log","--all","--max-count=20","--format=%H","--",path],cancellationToken:ct); var found=false;
            foreach(var commit in commits.Stdout.Split('\n').Where(s=>s.Length>0))
            {
                foreach(var spec in new[]{commit+":"+path,commit+"^:"+path})
                {
                    var shown=await git.RunAsync(Directory,["show",spec],cancellationToken:ct); if(shown.Code!=0) continue;
                    try
                    {
                        var obj=JsonNode.Parse(shown.Stdout); if(obj?["repos"] is JsonArray repos) foreach(var r in repos) if(r?["url"] is JsonValue value && value.TryGetValue<string>(out var url) && url.Trim().Length>0) urls.Add(url.Trim());
                        found=true;
                    }
                    catch(Exception e) when(e is System.Text.Json.JsonException or InvalidOperationException) { }
                    if(found) break;
                }
                if(found) break;
            }
        }
        return new(names,urls);
    }
}
public sealed record DeletedProfiles(IReadOnlySet<string> Names,IReadOnlySet<string> Urls) { public override string ToString()=>"DeletedProfiles"; }
