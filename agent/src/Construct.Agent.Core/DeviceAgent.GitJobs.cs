using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Git;
using Construct.Agent.State;

namespace Construct.Agent;

/// <summary>
/// Jobs that clone the VM's project repos, started from this device (plan 7.4): the agent adds the clone credentials
/// of this device's own git credential helper (<see cref="GitCloneCredentials"/>) to the job's secrets, which the
/// engine keeps in the job's RAM bag. Everything else in the request goes to the engine as it came, and the engine's
/// answer comes back with the hosts that had no credential. A request that already carries
/// <c>secrets.gitCloneCredentialsB64</c> keeps it. Only the user's own VMs get credentials: an admin acting on someone
/// else's VM sends none. Gathering never fails the start; whatever it cannot read means no credentials.
/// </summary>
public sealed partial class DeviceAgent
{
    private const string CredentialsField = "gitCloneCredentialsB64";

    private GitCloneCredentials Git => new(_options.GitSearchPath);

    /// <summary><c>POST /vms/{vm}/jobs</c>: credentials for a <c>provision</c> or <c>reinstall</c>; any other kind goes unchanged.</summary>
    public async Task<EngineAnswer> StartVmJobAsync(string engineId, string vm, JsonObject body, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        var found = GitCloneCredentialSet.None;
        if (Kind(body) is "provision" or "reinstall" && !Carries(body))
            found = await ForVmAsync(entry, vm, Projects(body["desired"]), ct).ConfigureAwait(false);
        return await ForwardAsync(entry, $"api/v1/vms/{Uri.EscapeDataString(vm)}/jobs", With(body, found), found, ct).ConfigureAwait(false);
    }

    /// <summary><c>POST /vms</c>: a create with settings provisions the new VM, which is the caller's own; credentials for its profiles.</summary>
    public async Task<EngineAnswer> CreateVmAsync(string engineId, JsonObject body, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        var found = GitCloneCredentialSet.None;
        if (body["desired"] is JsonObject desired && !Carries(body))
            found = await GatherAsync(entry, Projects(desired) ?? [], ct).ConfigureAwait(false);
        return await ForwardAsync(entry, "api/v1/vms", With(body, found), found, ct).ConfigureAwait(false);
    }

    /// <summary><c>POST /jobs/{id}/retry</c>: a failed provision or reinstall runs again with the VM's stored settings.</summary>
    public async Task<EngineAnswer> RetryJobAsync(string engineId, string id, JsonObject? body, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        body ??= new JsonObject();
        var found = GitCloneCredentialSet.None;
        if (!Carries(body))
        {
            // The list, not GET /jobs/{id}: reading a finished job by id can hand out its one-time secret.
            JsonNode? job = null;
            try
            {
                var failed = await EngineListAsync(entry, "api/v1/jobs?state=failed&limit=200", ct).ConfigureAwait(false);
                job = failed?.FirstOrDefault(j => Str(j, "id") == id);
            }
            catch (AgentFailure) { }

            if (job is not null && Str(job, "kind") is "provision" or "reinstall" && Str(job, "vmName") is { Length: > 0 } vm)
                found = await ForVmAsync(entry, vm, null, ct).ConfigureAwait(false);
        }

        return await ForwardAsync(entry, $"api/v1/jobs/{Uri.EscapeDataString(id)}/retry", With(body, found), found, ct).ConfigureAwait(false);
    }

    /// <summary>The credentials for a job on an existing VM: only when it is this user's, for the given or the stored profiles.</summary>
    private async Task<GitCloneCredentialSet> ForVmAsync(EngineEntry entry, string vm, IReadOnlyList<string>? projects, CancellationToken ct)
    {
        try
        {
            var me = Str(await EngineSendAsync(entry, HttpMethod.Get, "api/v1/whoami", null, ct).ConfigureAwait(false), "name");
            var owner = Str(await EngineSendAsync(entry, HttpMethod.Get, $"api/v1/vms/{Uri.EscapeDataString(vm)}", null, ct).ConfigureAwait(false), "owner");
            if (me.Length == 0 || !string.Equals(me, owner, StringComparison.OrdinalIgnoreCase)) return GitCloneCredentialSet.None;
            projects ??= Projects((await EngineSendAsync(entry, HttpMethod.Get, $"api/v1/vms/{Uri.EscapeDataString(vm)}/desired", null, ct)
                .ConfigureAwait(false))?["desired"]) ?? [];
        }
        catch (AgentFailure) { return GitCloneCredentialSet.None; }

        return await GatherAsync(entry, projects, ct).ConfigureAwait(false);
    }

    /// <summary>The repo URLs of the profiles (as this user reads them from the engine), then the helper's credentials for their hosts.</summary>
    private async Task<GitCloneCredentialSet> GatherAsync(EngineEntry entry, IReadOnlyList<string> projects, CancellationToken ct)
    {
        var urls = new List<string>();
        foreach (var name in projects.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var profile = await EngineSendAsync(entry, HttpMethod.Get, "api/v1/profiles/" + Uri.EscapeDataString(name), null, ct).ConfigureAwait(false);
                foreach (var repo in profile?["profile"]?["repos"] as JsonArray ?? [])
                    if (repo?["url"] is JsonValue v && v.TryGetValue<string>(out var url)) urls.Add(url);
            }
            catch (AgentFailure) { /* a profile the engine does not have declares no repos */ }
        }

        return await Git.GatherAsync(urls, ct).ConfigureAwait(false);
    }

    private static string? Kind(JsonObject body) => body["kind"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Carries(JsonObject body) => body["secrets"] is JsonObject s && s[CredentialsField] is not null;

    private static IReadOnlyList<string>? Projects(JsonNode? desired) =>
        desired?["projects"] is JsonArray list ? list.Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToArray() : null;

    /// <summary>The body with the credentials added to its <c>secrets</c>; the same body when there are none.</summary>
    private static JsonObject With(JsonObject body, GitCloneCredentialSet found)
    {
        if (found.CredentialsB64 is not { } credentials) return body;
        var copy = (JsonObject)body.DeepClone();
        if (copy["secrets"] is not JsonObject secrets) copy["secrets"] = secrets = new JsonObject();
        secrets[CredentialsField] = credentials;
        return copy;
    }

    private async Task<JsonArray?> EngineListAsync(EngineEntry entry, string path, CancellationToken ct)
    {
        using var http = EngineClientFor(entry.Id) ?? throw NoEngine();
        http.Timeout = TimeSpan.FromSeconds(30);
        try
        {
            using var response = await http.GetAsync(path, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonArray : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// The request to the engine and its answer as it is: status and body. A JSON object that succeeded gets
    /// <c>hostsWithoutCredential</c>; a refusal comes back unchanged.
    /// </summary>
    private async Task<EngineAnswer> ForwardAsync(EngineEntry entry, string path, JsonObject body, GitCloneCredentialSet found, CancellationToken ct)
    {
        using var http = EngineClientFor(entry.Id) ?? throw NoEngine();
        http.Timeout = TimeSpan.FromSeconds(60);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new AgentFailure(502, "engine-unreachable", ex.Message); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AgentFailure(504, "engine-timeout", "The engine did not answer in time."); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var type = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            if (response.IsSuccessStatusCode && TryObject(text) is { } answer)
            {
                answer["hostsWithoutCredential"] = new JsonArray(found.HostsWithoutCredential.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
                return new EngineAnswer((int)response.StatusCode, answer.ToJsonString(), "application/json; charset=utf-8");
            }

            return new EngineAnswer((int)response.StatusCode, text, type);
        }
    }

    private static JsonObject? TryObject(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>The engine's answer to a job start, as it came (status, body, content type).</summary>
public sealed record EngineAnswer(int Status, string Body, string ContentType);

/// <summary>
/// What the agent's job routes answer on success: the engine's own answer (for a create, the create's fields too) plus
/// the HTTP(S) hosts of the VM's repos that had no working credential on this device. A refusal is the engine's problem.
/// </summary>
public sealed record JobStartedWithCredentials(string JobId, string? Kind, bool? Replayed, IReadOnlyList<string> HostsWithoutCredential);
