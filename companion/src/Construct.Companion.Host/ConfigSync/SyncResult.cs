using System.Text.Json.Serialization;
using Construct.Companion.Core.ConfigSync;
namespace Construct.Companion.Host.ConfigSync;

// Serialized into the panel's configSync state; parity-pinned by ConfigSyncParityTests.
public sealed class SyncResult
{
    public bool Ok { get; set; }
    public bool Ran { get; set; }
    public bool Conflict { get; set; }
    public bool Blocked { get; set; }
    public string? BlockedReason { get; set; }
    public List<ProfileReason> SkippedInvalid { get; } = [];
    public bool Merged { get; set; }
    public bool Seeded { get; set; }
    public WriteBackResult WriteBack { get; set; } = new();
    public List<string> Warnings { get; } = [];
    public bool? VmReadOk { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public bool LockBusy { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? Reason { get; init; }
    public static SyncResult Busy(string reason) => new() { Ok = true, LockBusy = true, Reason = reason };
}
