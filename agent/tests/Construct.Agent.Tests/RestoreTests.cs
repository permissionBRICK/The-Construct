using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests;

/// <summary>
/// Restoring a stored backup from a device (plan 8.2, 10.2): the agent unwraps the backup's key with the
/// account's <c>B</c> and starts the engine's restore job with it; the key reaches only the job.
/// </summary>
public sealed class RestoreTests
{
    private const string BackupKey = "AGE-SECRET-KEY-1QQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQQ";

    /// <summary>Records what the engine sends to a guest; every command succeeds.</summary>
    private sealed class RecordingSsh : ISshRunner
    {
        public ConcurrentQueue<(string Command, string? Input)> Calls { get; } = new();

        public Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Enqueue((command, standardInput));
            return Task.FromResult(0);
        }

        public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_agent_unwraps_the_backup_key_with_B_and_hands_it_only_to_the_restore_job()
    {
        var ssh = new RecordingSsh();
        using var engine = new EngineHost(services => services.AddSingleton<ISshRunner>(ssh));
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop" });
        await CreateVmAsync(laptop, engineId);

        var publicPem = await laptop.Host.Agent.BackupPublicKeyAsync(CancellationToken.None);
        var good = await StoreBackupAsync(engine, publicPem);
        using var other = RSA.Create(3072);
        var foreign = await StoreBackupAsync(engine, other.ExportSubjectPublicKeyInfoPem());

        // A backup this account's key does not open is refused before any job.
        using (var refused = await laptop.Api.PostAsJsonAsync($"api/engines/{engineId}/vms/dev/backups/{foreign}/restore", new { requestId = "r0" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("backup-key-not-held", await refused.Content.ReadAsStringAsync());
        }

        var started = await laptop.PostAsync($"api/engines/{engineId}/vms/dev/backups/{good}/restore", new { requestId = "r1" });
        var jobId = started["jobId"]!.GetValue<string>();
        Assert.False(started["replayed"]!.GetValue<bool>());
        Assert.DoesNotContain(BackupKey, started.ToJsonString());
        await WaitAsync(laptop, engineId, jobId);

        var job = await laptop.GetAsync($"api/engines/{engineId}/api/v1/jobs/{jobId}");
        Assert.Equal("restore", job["kind"]!.GetValue<string>());
        Assert.DoesNotContain(BackupKey, job.ToJsonString());
        Assert.Contains(ssh.Calls, c => c.Input?.Contains(BackupKey) == true);
        Assert.DoesNotContain(ssh.Calls, c => c.Command.Contains(BackupKey));
        Assert.DoesNotContain(BackupKey, laptop.StateFileText());
    }

    private static async Task<string> StoreBackupAsync(EngineHost engine, string publicPem)
    {
        using var recipient = RSA.Create();
        recipient.ImportFromPem(publicPem);
        var id = Guid.NewGuid().ToString("n");
        var keys = new JsonObject
        {
            ["format"] = "construct-backup-keys/1", ["backupId"] = id, ["createdAt"] = "2026-11-04T10:00:00Z",
            ["recipients"] = new JsonArray(
                new JsonObject { ["fingerprint"] = new string('0', 64), ["label"] = "other", ["wrapped"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(384)) },
                new JsonObject
                {
                    ["fingerprint"] = Convert.ToHexStringLower(SHA256.HashData(recipient.ExportSubjectPublicKeyInfo())), ["label"] = "Construct",
                    ["wrapped"] = Convert.ToBase64String(recipient.Encrypt(Encoding.ASCII.GetBytes(BackupKey), RSAEncryptionPadding.OaepSHA256)),
                }),
        }.ToJsonString();
        var now = DateTimeOffset.UtcNow;
        await engine.Services.GetRequiredService<IBackupStore>().InsertAsync(
            new ConfigBackup(id, "alice", "dev", "manual", BackupStates.Complete, now, now, 10, new string('a', 64), null, keys), CancellationToken.None);
        return id;
    }

    private static async Task CreateVmAsync(TestAgent laptop, string engineId)
    {
        using var created = await laptop.Api.PostAsJsonAsync($"api/engines/{engineId}/api/v1/vms", new { name = "dev", cpu = 2, ramGb = 4, diskGb = 40 });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        await WaitAsync(laptop, engineId, JsonNode.Parse(await created.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>());
    }

    private static async Task WaitAsync(TestAgent laptop, string engineId, string jobId)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while ((await laptop.GetAsync($"api/engines/{engineId}/api/v1/jobs/{jobId}"))["state"]!.GetValue<string>() is "queued" or "running")
        {
            Assert.True(DateTime.UtcNow < until, "The job did not end in time.");
            await Task.Delay(100);
        }
    }
}
