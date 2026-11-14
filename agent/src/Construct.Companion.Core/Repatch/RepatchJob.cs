using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Core.Repatch;

public static class RepatchProtocol
{
    public static JsonObject ParseStatus(string stdout)
    {
        string? Pick(string key) => Regex.Matches(stdout, "^" + key + "=(patched|stock|unknown|absent)\\s*$", RegexOptions.Multiline).LastOrDefault()?.Groups[1].Value;
        return new() { ["partial"] = Pick("CONSTRUCT_PARTIAL_STATUS"), ["gate"] = Pick("CONSTRUCT_GATE_STATUS") };
    }
    public static JsonObject PlanStartupActions(bool streamingOn, bool micOn, bool micLive, bool hasHostAudio) => new()
    { ["runPass"] = streamingOn || micOn && micLive, ["passMicOn"] = micOn && micLive, ["retryAutoArm"] = micOn && !hasHostAudio };
    public static JsonObject DecideRepairs(JsonObject status, bool streamingOn, bool micOn) => new()
    { ["streaming"] = streamingOn && status.Str("partial") == "stock", ["mic"] = micOn && status.Str("gate") == "stock" };
}
public sealed class RepatchJob(ISshTransport ssh)
{
    public async Task<JsonObject> RunAsync(bool streamingOn, bool micOn, CancellationToken token = default)
    {
        var result = new JsonObject { ["reachable"] = false, ["status"] = RepatchProtocol.ParseStatus(""), ["repaired"] = new JsonObject { ["streaming"] = false, ["mic"] = false }, ["error"] = null };
        if (!streamingOn && !micOn) return result;
        try
        {
            if ((await ssh.RunRemoteScriptAsync("true", TimeSpan.FromSeconds(12), token).ConfigureAwait(false)).Code != 0) return result;
            result["reachable"] = true;
            ProcessResult status;
            try { status = await ssh.RunRemoteScriptAsync(GuestScripts.Render("construct-patch-status"), TimeSpan.FromSeconds(20), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { result["error"] = "status-exception"; return result; }
            if (status.Code != 0) { result["error"] = "status-failed"; return result; }
            var parsed = RepatchProtocol.ParseStatus(status.Stdout); result["status"] = parsed;
            var repairs = RepatchProtocol.DecideRepairs(parsed, streamingOn, micOn);
            foreach (var (key, script, marker) in new[] { ("streaming", GuestScripts.Render("construct-partial-streaming-enable"), "CONSTRUCT_PARTIAL_PATCHED"), ("mic", AudioProtocol.EnableScript(), "CONSTRUCT_GATE_PATCHED") })
            {
                if (!repairs.True(key)) continue;
                try
                {
                    var response = await ssh.RunRemoteScriptAsync(script, TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                    result["repaired"]![key] = response.Code == 0 && AudioProtocol.ConfirmPatched(marker, response.Stdout);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { result["error"] = "unreachable"; }
        return result;
    }
}
