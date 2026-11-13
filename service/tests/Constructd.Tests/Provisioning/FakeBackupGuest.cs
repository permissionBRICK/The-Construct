using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// A scripted guest that also answers <c>construct backup</c> the way <c>bin/construct-backup.sh</c> does:
/// recipients are enrolled and removed, <c>create</c> uploads a backup to the host through the real
/// routes with the VM token (so it consumes the host's request), its keys file wrapped for every
/// recipient with RSA-OAEP-SHA256; <c>verify</c> and <c>restore</c> check the key on the first line of
/// stdin, and a restore brings the backup's recipients back. A new VM (see <see cref="Incarnation"/>)
/// starts with no recipients.
/// </summary>
public sealed class FakeBackupGuest : FakeSshRunner
{
    /// <summary>Fingerprint → public key and label.</summary>
    public Dictionary<string, (string Pem, string Label)> Recipients { get; } = new(StringComparer.Ordinal);
    /// <summary>Each backup this guest made: its key E and the recipients it carries.</summary>
    public Dictionary<string, (string Key, Dictionary<string, (string Pem, string Label)> Recipients)> Made { get; } = new(StringComparer.Ordinal);
    public List<string> Verified { get; } = [];
    public List<string> Restored { get; } = [];

    /// <summary>The guest's client for the host's backup routes (its VM token).</summary>
    public Func<HttpClient>? Host { get; set; }
    public string VmName { get; set; } = ProvisionHarness.Vm;

    /// <summary>Changes when the VM is created again; the guest then forgets what it had.</summary>
    public Func<string>? Incarnation { get; set; }
    private string? _seen;

    public int AddExit { get; set; }
    public int CreateExit { get; set; }
    public int VerifyExit { get; set; }
    public int RestoreExit { get; set; }
    public int RemoveExit { get; set; }

    /// <summary>Takes the current incarnation as this guest's own (a host restarted around a VM that survived).</summary>
    public void Adopt() => _seen = Incarnation?.Invoke();

    public override async Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
    {
        var inner = Unwrap(command);
        if (!inner.Contains("construct-backup.sh", StringComparison.Ordinal))
            return await base.RunAsync(target, command, standardInput, lines, timeout, ct);

        Calls.Enqueue(new SshCall(target, command, standardInput));
        if (Incarnation?.Invoke() is { } now && now != _seen)
        {
            _seen = now;
            Recipients.Clear();
        }

        var args = inner[(inner.IndexOf("construct-backup.sh", StringComparison.Ordinal) + "construct-backup.sh".Length)..].Trim().Split(' ');
        var step = args[0] switch
        {
            "recipients" => "backup-" + args[1],
            "create" => "backup-create",
            "verify" => "backup-verify",
            _ => args[0],
        };
        if (HoldAt == step)
        {
            HoldAt = null;
            Held.TrySetResult();
            await Release.Task;
        }

        var key = (standardInput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        switch (step)
        {
            case "backup-add":
            {
                if (AddExit != 0) return AddExit;
                var pem = standardInput![standardInput!.IndexOf("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal)..];
                var fp = BackupJobKey.PublicFingerprint(pem)!;
                var label = inner[(inner.IndexOf("--label ", StringComparison.Ordinal) + 8)..].Trim().Trim('\'');
                Recipients[fp] = (pem, label);
                lines?.Report($"construct backup: enrolled {BackupJobKey.Label} ({fp})");
                lines?.Report(fp);
                return 0;
            }
            case "backup-list":
                lines?.Report(new JsonArray(Recipients.Select(r => (JsonNode)new JsonObject { ["fingerprint"] = r.Key, ["label"] = r.Value.Label, ["addedAt"] = null }).ToArray()).ToJsonString());
                return 0;
            case "backup-remove":
                if (RemoveExit != 0) return RemoveExit;
                Recipients.Remove(args[2]);
                return 0;
            case "backup-create":
                if (CreateExit != 0) return CreateExit;
                if (Recipients.Count == 0) return 3;
                return await CreateAsync(lines);
            case "backup-verify":
                Verified.Add(args[2]);
                if (VerifyExit != 0) return VerifyExit;
                return Made.TryGetValue(args[2], out var made) && made.Key == key ? 0 : 8;
            case "restore":
            {
                if (RestoreExit != 0) return RestoreExit;
                if (!Made.TryGetValue(args[2], out var backup) || backup.Key != key) return 8;
                Restored.Add(args[2]);
                foreach (var (fp, recipient) in backup.Recipients) Recipients.TryAdd(fp, recipient);
                lines?.Report($"construct backup: backup {args[2]} restored");
                return 0;
            }
            default:
                return 2;
        }
    }

    private async Task<int> CreateAsync(IProgress<string>? lines)
    {
        var host = Host?.Invoke() ?? throw new InvalidOperationException("The guest has no host client.");
        var e = "AGE-SECRET-KEY-1" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var opened = await host.PostAsJsonAsync($"/api/v1/vms/{VmName}/backups", new { trigger = "pre-reinstall", estimatedBytes = 100 });
        if (opened.StatusCode != HttpStatusCode.Created) return 6;
        var id = (await opened.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        var keys = new JsonObject
        {
            ["format"] = "construct-backup-keys/1", ["backupId"] = id, ["createdAt"] = "2026-11-13T10:00:00Z",
            ["recipients"] = new JsonArray(Recipients.Select(r => (JsonNode)new JsonObject
            {
                ["fingerprint"] = r.Key, ["label"] = r.Value.Label, ["wrapped"] = BackupJobKey.Wrap(e, r.Value.Pem),
            }).ToArray()),
        };
        using var put = await host.PutAsync($"/api/v1/vms/{VmName}/backups/{id}/keys",
            new StringContent(keys.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        if (put.StatusCode != HttpStatusCode.NoContent) return 7;
        var content = new ByteArrayContent(RandomNumberGenerator.GetBytes(256));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var upload = await host.PutAsync($"/api/v1/vms/{VmName}/backups/{id}/content", content);
        if (upload.StatusCode != HttpStatusCode.OK) return 7;
        Made[id] = (e, new Dictionary<string, (string, string)>(Recipients, StringComparer.Ordinal));
        lines?.Report($"{{\"id\":\"{id}\",\"sizeBytes\":256,\"sha256\":\"x\",\"trigger\":\"pre-reinstall\",\"gitCredentialHosts\":[]}}");
        return 0;
    }
}
