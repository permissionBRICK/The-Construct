using System.Globalization;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;

namespace Constructd.Proxmox;

/// <summary>Child operations keep dependency output out of errors, progress and audit logs.</summary>
internal sealed class ProxmoxCommands(IProcessRunner runner, ConstructdOptions options)
{
    internal string VmPath(int id) => $"/nodes/{ProxmoxDriver.ResolveNode(options)}/qemu/{Number(id)}";
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    internal static string? String(JsonElement json, string key) => json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string? Property(string? value, string key) => value?.Split(',')
        .FirstOrDefault(part => part.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
    internal static string? Incarnation(JsonElement config) =>
        Guid.TryParse(Property(String(config, "smbios1"), "uuid"), out var id) ? id.ToString() : null;
    internal static bool HasChildTag(JsonElement config) => (String(config, "tags") ?? "").Split(';').Contains("construct-child", StringComparer.Ordinal);

    internal async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct,
        TimeSpan? timeout = null, string? input = null)
    {
        try { return await runner.RunAsync(file, args, input, timeout ?? TimeSpan.FromMinutes(5), null, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw Failure(); }
    }
    internal async Task QmAsync(IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (!(await RunAsync(options.Proxmox.QmPath, args, ct, timeout)).Succeeded) throw Failure();
    }
    internal async Task<JsonElement> QueryAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await RunAsync(options.Proxmox.PveshPath, [.. args, "--output-format", "json"], ct, TimeSpan.FromSeconds(60));
        if (!result.Succeeded) throw Failure();
        try { using var doc = JsonDocument.Parse(result.StandardOutput); return doc.RootElement.Clone(); }
        catch (JsonException) { throw Failure(); }
    }
    internal Task<JsonElement> ConfigAsync(int id, CancellationToken ct) => QueryAsync(["get", VmPath(id) + "/config"], ct);
    internal async Task<VmState> StateAsync(int id, CancellationToken ct) =>
        ProxmoxDriver.MapState(await QueryAsync(["get", VmPath(id) + "/status/current"], ct));
    // The node's own guest list, not /cluster/resources: the cluster view is pvestatd's cache and
    // lags a freshly created VM by up to ten seconds, which made the incarnation read-back right
    // after `qm create` fail. The node list reads the config files directly.
    internal Task<JsonElement> ResourcesAsync(CancellationToken ct) =>
        QueryAsync(["get", "/nodes/" + ProxmoxDriver.ResolveNode(options) + "/qemu"], ct);
    internal int? Find(JsonElement resources, string name)
    {
        ArgumentGuard.VmName(name);
        if (resources.ValueKind != JsonValueKind.Array) throw Failure();
        // Entries from the node list carry no type/node fields; cluster-shaped entries must match them.
        var matches = resources.EnumerateArray().Where(e => (String(e, "type") ?? "qemu") == "qemu" &&
            (String(e, "node") is not { } node || string.Equals(node, ProxmoxDriver.ResolveNode(options), StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(String(e, "name"), name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new ChildValidationException("vm-identity-ambiguous", "vm");
        if (matches.Length == 0) return null;
        return matches[0].TryGetProperty("vmid", out var value) && value.TryGetInt32(out var id) && id > 0 ? id : throw Failure();
    }
    internal async Task<int?> FindAsync(string name, CancellationToken ct)
    { ArgumentGuard.VmName(name); return Find(await ResourcesAsync(ct), name); }
    internal async Task<int> RequireAsync(string name, CancellationToken ct) => await FindAsync(name, ct) ?? throw Failure();
    internal static ProxmoxOperationException Failure() => new("child-operation", "");
}
