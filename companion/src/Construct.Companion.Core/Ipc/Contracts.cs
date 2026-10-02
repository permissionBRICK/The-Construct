using System.Text.Json;
using System.Text.Json.Serialization;
using Construct.Companion.Core.Vault;

namespace Construct.Companion.Core.Ipc;

public static class IpcJson
{
    public const int ApiVersion = 1;
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public sealed record Endpoint(int V, int Port, string Token, int Pid, DateTimeOffset StartedAt,
    string Version, int IpcApiVersion = IpcJson.ApiVersion)
{
    public override string ToString() => "Endpoint";
}
public sealed record InstallManifest(int SchemaVersion, string Commit, string PackageVersion,
    string Source, string? ReleaseTag, DateTimeOffset InstalledAt, int IpcApiVersion = IpcJson.ApiVersion);
public sealed record Health(bool Ok, string Version, int Pid, DateTimeOffset StartedAt,
    int IpcApiVersion = IpcJson.ApiVersion);
public sealed record CompanionState(string? ActiveInstance, IReadOnlyList<string> Instances,
    IReadOnlyDictionary<string, Snapshot> Snapshots);
public sealed record Snapshot(JsonElement? State, JsonElement? Settings, JsonElement? Audio,
    JsonElement? Forwards, JsonElement? Children, JsonElement? IdlePolicy, JsonElement? HostAdminOffer);
public sealed record InstanceMessage(string Instance, JsonElement Message);
public sealed record AcceptedResponse(bool Accepted = true);
public sealed record RemoteHost(string Slug, string Url, string Auth, bool Pinned, bool? Admin);
public sealed record AddRemoteHost(string Url, string? Token = null, string? Fingerprint = null)
{
    public override string ToString() => "AddRemoteHost";
}
public sealed record UiActivation(string View,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Instance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Host = null)
{
    // In-process only: the IPC route already queued the opening refresh.
    [JsonIgnore] public bool RefreshScheduled { get; init; }
}
public sealed record QuitRequest(string Reason);
// GET /v1/vault/approvals: what T3 Code Desktop shows to approve key vault requests inline. The native
// dialog's texts and where a request came from (times in ms), never a value. POST /v1/vault/approvals/{id}
// takes {"decision":"approve"|"deny"}.
public sealed record VaultApprovalList(IReadOnlyList<VaultApprovalItem> Approvals);
public sealed record VaultApprovalItem(string Id, string Instance, string Vm, string Kind, string? Host, string? RequestId, string? HostRequestId, string Op,
    string Title, string Message, string Action, string Deny, IReadOnlyList<string> Names, long CreatedAt, long? Deadline)
{
    public static VaultApprovalItem From(VaultPendingApproval a) => new(a.Id, a.Instance, a.Vm, a.Kind, a.Host, a.RequestId, a.HostRequestId, a.Op,
        a.Title, a.Message, a.Action, a.Deny, a.Names, a.CreatedAt.ToUnixTimeMilliseconds(), a.Deadline?.ToUnixTimeMilliseconds());
}
public sealed record Problem(string Type, string Title, int Status, string Code, string? Detail = null);
public sealed record ForwardSettings(bool Enabled = true, string HostLabel = "");
public sealed record CompanionSettings(int V = 1, string? ActiveInstance = null, string UiTheme = "",
    string MicDevice = "", bool Notifications = true, ForwardSettings? Forwards = null,
    int RepatchDelaySeconds = 45, bool Autostart = true, bool Debug = false, string ScriptsDir = "",
    JsonElement? Windows = null)
{
    public ForwardSettings Forwards { get; init; } = Forwards ?? new();
}
// PUT is a partial merge; absence must remain distinguishable from null/default values.
public sealed record SettingsPatch
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Values { get; init; } = [];
}
