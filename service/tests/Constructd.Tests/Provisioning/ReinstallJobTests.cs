using System.Net;
using System.Text;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Tests.Backups;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// The <c>reinstall</c> and <c>restore</c> jobs against the fake driver and a scripted guest that runs
/// <c>construct backup</c> against the real backup routes: the whole reinstall, its failures before and
/// after the delete and their retries, the job key's reach, the host key, and Appendix C.1's engine kill.
/// </summary>
public sealed class ReinstallJobTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "construct-reinstall-" + Guid.NewGuid().ToString("n"));
    private readonly (string PrivatePem, string PublicPem, string Fingerprint) _pc = BackupJobKey.Create();
    private readonly List<ProvisionHarness> _hosts = [];
    private string? _token;

    private string Db => Path.Combine(_root, "constructd.db");

    private async Task<ProvisionHarness> HostAsync(FakeBackupGuest guest, bool createVm = true, ISshRunner? runner = null)
    {
        Directory.CreateDirectory(_root);
        var settings = new Dictionary<string, string?>
        {
            ["Constructd:HostAdmin:Source:RootDir"] = Path.Combine(_root, "source"),
            ["Constructd:Backups:RootDir"] = Path.Combine(_root, "backups"),
        };
        var h = await ProvisionHarness.StartAsync(guest, Db, createVm, settings, runner,
            services => services.AddSingleton<IFreeSpaceProbe>(new FakeFreeSpaceProbe()));
        _hosts.Add(h);
        _token ??= h.VmToken;
        var token = _token!;
        guest.Host = () => h.App.CreateVmTokenClient(token);
        var app = h.App;
        guest.Incarnation = () => app.GetHashCode() + ":" + app.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal));
        if (createVm)
        {
            guest.Adopt();
            guest.Recipients[_pc.Fingerprint] = (_pc.PublicPem, "PC");
        }

        return h;
    }

    private static Task<HttpResponseMessage> PostReinstallAsync(ProvisionHarness h, string requestId, bool desired = true) =>
        h.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs", new
        {
            kind = "reinstall", requestId, commit = ProvisionHarness.Commit, desired = desired ? ProvisionHarness.Desired() : null,
        });

    private static async Task<string> StartReinstallAsync(ProvisionHarness h, string requestId, bool desired = true)
    {
        var response = await PostReinstallAsync(h, requestId, desired);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<VmJobAcceptedResponse>()).JobId;
    }

    private static async Task<JobResponse> ReinstallAsync(ProvisionHarness h, string requestId, bool desired = true) =>
        await h.Client.WaitForJobAsync(await StartReinstallAsync(h, requestId, desired));

    private static async Task<JobResponse> RestoreAsync(ProvisionHarness h, string requestId, string backupId, string key) =>
        await h.Client.WaitForJobAsync(await StartRestoreAsync(h, requestId, backupId, key));

    private static async Task<string> StartRestoreAsync(ProvisionHarness h, string requestId, string backupId, string key)
    {
        var response = await h.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs",
            new { kind = "restore", requestId, backupId, secrets = new { backupKey = key } });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<VmJobAcceptedResponse>()).JobId;
    }

    private static T Result<T>(JobResponse job) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(job.Result), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static async Task<IReadOnlyList<ConfigBackup>> BackupsAsync(ProvisionHarness h) =>
        (await h.App.Service<IBackupStore>().ListAsync("alice", ProvisionHarness.Vm, default)).Where(b => b.IsComplete).ToList();

    private string JobRecipient(FakeBackupGuest guest, string backupId) =>
        guest.Made[backupId].Recipients.Single(r => r.Key != _pc.Fingerprint).Key;

    [Fact]
    public async Task A_reinstall_backs_up_deletes_creates_provisions_restores_and_leaves_no_job_key()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var target = (await h.App.Service<IProvisionTargets>().ResolveAsync(vm, default))!;
        await File.WriteAllTextAsync(target.KnownHostsFile!, $"{target.Host} ssh-ed25519 AAAAold\nother.test ssh-ed25519 AAAAkeep\n");

        var job = await ReinstallAsync(h, "reinstall-1");

        Assert.True(job.State == JobState.Succeeded, job.Error);
        var result = Result<ReinstallJobResult>(job);
        Assert.True(result.Succeeded);
        Assert.True(result.VmDeleted);
        Assert.True(result.Restored);
        var id = Assert.Single(guest.Made.Keys);
        Assert.Equal(id, result.BackupId);

        // Made for the user's key and the job's; verified before the delete, restored after the create.
        var jobKey = JobRecipient(guest, id);
        Assert.Contains(_pc.Fingerprint, guest.Made[id].Recipients.Keys);
        Assert.Equal([id], guest.Verified);
        Assert.Equal([id], guest.Restored);
        var calls = h.App.Driver.Calls.ToList();
        Assert.True(calls.LastIndexOf("remove:" + ProvisionHarness.Vm) < calls.LastIndexOf("create:" + ProvisionHarness.Vm));
        var commands = guest.Commands.ToList();
        Assert.True(commands.FindIndex(c => c.Contains(" verify --from-host", StringComparison.Ordinal))
            < commands.FindIndex(c => c.Contains("bin/provision.sh", StringComparison.Ordinal)));
        var restoreAt = commands.FindIndex(c => c.Contains(" restore --from-host", StringComparison.Ordinal));
        Assert.True(commands.FindIndex(c => c.Contains("DEFER_PROJECT_COMMANDS", StringComparison.Ordinal)) < restoreAt);
        Assert.True(restoreAt < commands.FindIndex(c => c.Contains("PROVISION_PHASE", StringComparison.Ordinal)));

        // The user's key came back with the backup; the job's is gone again.
        Assert.Equal([_pc.Fingerprint], guest.Recipients.Keys);
        Assert.DoesNotContain(jobKey, guest.Recipients.Keys);

        // The host kept it as the protected pre-reinstall backup, readable with the user's key.
        var backup = Assert.Single(await BackupsAsync(h));
        Assert.Equal(BackupTriggers.PreReinstall, backup.Trigger);
        Assert.False(backup.IsSlot);
        Assert.True(backup.IsPinned(h.App.Clock.UtcNow));
        Assert.Equal(guest.Made[id].Key, BackupJobKey.Unwrap(backup.KeysJson, id, _pc.PrivatePem));

        // A new VM token for the new VM, staged on stdin; the old host key of the address is gone, others stay.
        Assert.Contains(guest.Calls, c => c.StandardInput?.Contains(ProvisionScript.VmTokenVariable + "=", StringComparison.Ordinal) == true);
        Assert.NotEqual(vm.VmTokenHash, (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!.VmTokenHash);
        Assert.Equal("other.test ssh-ed25519 AAAAkeep\n", await File.ReadAllTextAsync(target.KnownHostsFile!));
    }

    [Fact]
    public async Task A_reinstall_whose_restore_brings_the_host_keys_back_keeps_the_stored_key()
    {
        var guest = new FakeBackupGuest { RestoreHostKeys = "restored SSH host keys" };
        var h = await HostAsync(guest);
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var target = (await h.App.Service<IProvisionTargets>().ResolveAsync(vm, default))!;
        var known = target.KnownHostsFile!;
        await File.WriteAllTextAsync(known, $"{target.Host} ssh-ed25519 AAAAold\nother.test ssh-ed25519 AAAAkeep\n");

        var job = await ReinstallAsync(h, "reinstall-1");

        Assert.True(job.State == JobState.Succeeded, job.Error);
        Assert.Equal($"{target.Host} ssh-ed25519 AAAAold\nother.test ssh-ed25519 AAAAkeep\n", await File.ReadAllTextAsync(known));
        // Between the create and the restore the new VM was trusted from the job's own file; after it, from the stored one.
        var calls = guest.Calls.ToList();
        var restoreAt = calls.FindIndex(c => c.Command.Contains(" restore --from-host", StringComparison.Ordinal));
        var provisionAt = calls.FindIndex(c => c.Command.Contains("bin/provision.sh", StringComparison.Ordinal));
        Assert.All(calls[provisionAt..(restoreAt + 1)], c => Assert.Equal(known + ".reinstall-" + job.Id, c.Target.KnownHostsFile));
        Assert.All(calls[(restoreAt + 1)..], c => Assert.Equal(known, c.Target.KnownHostsFile));
        Assert.Contains(calls[(restoreAt + 1)..], c => c.Command.Contains("PROVISION_PHASE", StringComparison.Ordinal));
        Assert.False(File.Exists(known + ".reinstall-" + job.Id));
    }

    [Fact]
    public async Task A_reinstall_that_fails_after_the_create_lets_the_next_connection_learn_the_key()
    {
        var guest = new FakeBackupGuest { Provision = _ => (1, ["boom"]), RestoreHostKeys = "restored SSH host keys" };
        var h = await HostAsync(guest);
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var target = (await h.App.Service<IProvisionTargets>().ResolveAsync(vm, default))!;
        var known = target.KnownHostsFile!;
        await File.WriteAllTextAsync(known, $"{target.Host} ssh-ed25519 AAAAold\nother.test ssh-ed25519 AAAAkeep\n");

        var job = await ReinstallAsync(h, "reinstall-1");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("other.test ssh-ed25519 AAAAkeep\n", await File.ReadAllTextAsync(known));
        Assert.False(File.Exists(known + ".reinstall-" + job.Id));
    }

    [Fact]
    public async Task A_restore_job_that_changed_the_host_keys_lets_the_next_connection_learn_them()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        Assert.Equal(JobState.Succeeded, (await ReinstallAsync(h, "reinstall-1")).State);
        var backup = Assert.Single(guest.Made.Keys);
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var target = (await h.App.Service<IProvisionTargets>().ResolveAsync(vm, default))!;
        await File.WriteAllTextAsync(target.KnownHostsFile!, $"{target.Host} ssh-ed25519 AAAAnow\n");

        guest.RestoreHostKeys = "SSH host keys already match the backup";
        Assert.Equal(JobState.Succeeded, (await RestoreAsync(h, "restore-1", backup, guest.Made[backup].Key)).State);
        Assert.Equal($"{target.Host} ssh-ed25519 AAAAnow\n", await File.ReadAllTextAsync(target.KnownHostsFile!));

        guest.RestoreHostKeys = "restored SSH host keys";
        Assert.Equal(JobState.Succeeded, (await RestoreAsync(h, "restore-2", backup, guest.Made[backup].Key)).State);
        Assert.Equal("", await File.ReadAllTextAsync(target.KnownHostsFile!));
    }

    [Fact]
    public async Task A_provision_never_replaces_a_stored_host_key()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var target = (await h.App.Service<IProvisionTargets>().ResolveAsync(vm, default))!;
        await File.WriteAllTextAsync(target.KnownHostsFile!, $"{target.Host} ssh-ed25519 AAAAold\n");
        Assert.Equal(JobState.Succeeded, (await h.RunAsync("provision-1")).State);
        Assert.Equal($"{target.Host} ssh-ed25519 AAAAold\n", await File.ReadAllTextAsync(target.KnownHostsFile!));
    }

    [Fact]
    public async Task A_failure_before_the_delete_leaves_the_VM_and_removes_the_job_key()
    {
        var guest = new FakeBackupGuest { VerifyExit = 8 };
        var h = await HostAsync(guest);

        var job = await ReinstallAsync(h, "reinstall-1");

        Assert.Equal(JobState.Failed, job.State);
        var result = Result<ReinstallJobResult>(job);
        Assert.Equal("backup", result.FailedStep);
        Assert.Equal("backup-verify-failed", result.Code);
        Assert.False(result.VmDeleted);
        Assert.DoesNotContain(h.App.Driver.Calls, c => c.StartsWith("remove:", StringComparison.Ordinal));
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(ProvisionHarness.Vm));
        Assert.Equal([_pc.Fingerprint], guest.Recipients.Keys);
        // The backup it made stays on the host.
        Assert.Equal(result.BackupId, Assert.Single(await BackupsAsync(h)).Id);

        // The next reinstall starts afresh and succeeds.
        guest.VerifyExit = 0;
        var again = await ReinstallAsync(h, "reinstall-2");
        Assert.True(again.State == JobState.Succeeded, again.Error);
        Assert.Equal([_pc.Fingerprint], guest.Recipients.Keys);
    }

    [Fact]
    public async Task A_VM_that_cannot_back_itself_up_is_not_deleted()
    {
        var guest = new FakeBackupGuest { CreateExit = 4 };
        var h = await HostAsync(guest);
        var job = await ReinstallAsync(h, "reinstall-1");
        Assert.Equal("backup-failed", Result<ReinstallJobResult>(job).Code);
        Assert.DoesNotContain(h.App.Driver.Calls, c => c.StartsWith("remove:", StringComparison.Ordinal));
        Assert.Equal([_pc.Fingerprint], guest.Recipients.Keys);
    }

    [Fact]
    public async Task A_VM_whose_backup_only_the_job_key_opens_is_not_deleted()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        guest.Recipients.Clear();
        var job = await ReinstallAsync(h, "reinstall-1");
        Assert.Equal("backup-no-device-key", Result<ReinstallJobResult>(job).Code);
        Assert.DoesNotContain(h.App.Driver.Calls, c => c.StartsWith("remove:", StringComparison.Ordinal));
        Assert.Empty(guest.Recipients);
    }

    [Fact]
    public async Task A_restore_whose_job_key_cleanup_failed_is_not_run_again()
    {
        var guest = new FakeBackupGuest { RemoveExit = 1 };
        var h = await HostAsync(guest);
        var first = await ReinstallAsync(h, "reinstall-1");
        var result = Result<ReinstallJobResult>(first);
        Assert.Equal("recipient-remove-failed", result.Code);
        var id = result.BackupId!;
        Assert.Equal([id], guest.Restored);

        guest.RemoveExit = 0;
        var again = await RestoreAsync(h, "restore-1", id, guest.Made[id].Key);
        Assert.Equal("restore-already-ran", Result<RestoreJobResult>(again).Code);
        var retry = await ReinstallAsync(h, "reinstall-2", desired: false);
        Assert.Equal("restore-already-ran", Result<ReinstallJobResult>(retry).Code);
        Assert.Equal([id], guest.Restored);
    }

    [Fact]
    public async Task A_failure_after_the_delete_is_retried_from_create_with_the_same_backup_and_key()
    {
        var guest = new FakeBackupGuest { Provision = _ => (1, ["boom"]) };
        var h = await HostAsync(guest);

        var first = await ReinstallAsync(h, "reinstall-1");
        Assert.Equal(JobState.Failed, first.State);
        var failed = Result<ReinstallJobResult>(first);
        Assert.True(failed.VmDeleted);
        Assert.Equal("provision", failed.FailedStep);
        Assert.False(failed.Restored);

        guest.Provision = _ => (0, FakeSshRunner.Clean);
        var retry = await ReinstallAsync(h, "reinstall-2", desired: false);
        Assert.True(retry.State == JobState.Succeeded, retry.Error);
        var result = Result<ReinstallJobResult>(retry);
        Assert.Equal(failed.BackupId, result.BackupId);
        Assert.True(result.Restored);
        // No second backup, no second verify: the original one is restored.
        Assert.Equal(failed.BackupId, Assert.Single(guest.Made.Keys));
        Assert.Equal(failed.BackupId, Assert.Single(await BackupsAsync(h)).Id);
        Assert.Equal([failed.BackupId!], guest.Restored);
        Assert.Equal([_pc.Fingerprint], guest.Recipients.Keys);
        Assert.Equal(1, guest.Commands.Count(c => c.Contains(" create --trigger", StringComparison.Ordinal)));
    }

    private static async Task<HttpResponseMessage> RetryAsync(ProvisionHarness h, string jobId, object? body = null)
    {
        JobRetryTests.Installed(h);
        return await h.Client.PostJsonAsync($"/api/v1/jobs/{jobId}/retry", body ?? new { });
    }

    [Fact]
    public async Task Retrying_a_reinstall_that_failed_after_the_delete_uses_the_same_backup_once()
    {
        var guest = new FakeBackupGuest { Provision = _ => (1, ["boom"]) };
        var h = await HostAsync(guest);
        var first = await ReinstallAsync(h, "reinstall-1");
        var failed = Result<ReinstallJobResult>(first);
        Assert.True(failed.VmDeleted);

        guest.Provision = _ => (0, FakeSshRunner.Clean);
        var response = await RetryAsync(h, first.Id);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.ReadAsync<VmJobAcceptedResponse>();
        Assert.Equal(JobKinds.Reinstall, accepted.Kind);
        var retry = await h.Client.WaitForJobAsync(accepted.JobId);
        Assert.True(retry.State == JobState.Succeeded, retry.Error);
        Assert.Equal(failed.BackupId, Result<ReinstallJobResult>(retry).BackupId);
        Assert.Equal(1, guest.Commands.Count(c => c.Contains(" create --trigger", StringComparison.Ordinal)));

        // A second click answers the retry it started; nothing runs again.
        var creates = h.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal));
        var again = await (await RetryAsync(h, first.Id)).ReadAsync<VmJobAcceptedResponse>();
        Assert.True(again.Replayed);
        Assert.Equal(accepted.JobId, again.JobId);
        Assert.Equal(creates, h.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Retrying_a_reinstall_whose_restore_started_never_deletes_the_VM_again()
    {
        var guest = new FakeBackupGuest { RestoreExit = 10 };
        var h = await HostAsync(guest);
        var first = await ReinstallAsync(h, "reinstall-1");
        Assert.Equal("restore", Result<ReinstallJobResult>(first).FailedStep);

        guest.RestoreExit = 0;
        var creates = h.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal));
        var retry = await h.Client.WaitForJobAsync((await (await RetryAsync(h, first.Id)).ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal("restore-already-ran", Result<ReinstallJobResult>(retry).Code);
        Assert.Equal(creates, h.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)));
        Assert.Empty(guest.Restored);
    }

    [Fact]
    public async Task Retrying_a_restore_needs_the_key_again_and_restores_the_same_backup()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        var id = await MakeBackupAsync(h, guest);
        var wrong = await RestoreAsync(h, "restore-1", id, "AGE-SECRET-KEY-1WRONG");
        Assert.Equal(JobState.Failed, wrong.State);

        var noKey = await RetryAsync(h, wrong.Id);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        Assert.Contains("secrets.backupKey", await noKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var response = await RetryAsync(h, wrong.Id, new { secrets = new { backupKey = guest.Made[id].Key } });
        var retry = await h.Client.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.True(retry.State == JobState.Succeeded, retry.Error);
        Assert.Equal(id, Result<RestoreJobResult>(retry).BackupId);
        Assert.Equal([id], guest.Restored);
    }

    [Fact]
    public async Task A_restore_that_failed_halfway_is_not_run_again_by_a_retry()
    {
        var guest = new FakeBackupGuest { RestoreExit = 10 };
        var h = await HostAsync(guest);
        var first = await ReinstallAsync(h, "reinstall-1");
        Assert.Equal("restore", Result<ReinstallJobResult>(first).FailedStep);

        guest.RestoreExit = 0;
        var creates = h.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal));
        var retry = await ReinstallAsync(h, "reinstall-2", desired: false);
        Assert.Equal(JobState.Failed, retry.State);
        Assert.Equal("restore-already-ran", Result<ReinstallJobResult>(retry).Code);
        // The VM is neither deleted nor created again.
        Assert.Equal(creates, h.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)));
        Assert.Empty(guest.Restored);
        Assert.Single(await BackupsAsync(h));
    }

    [Fact]
    public async Task The_job_key_and_the_backup_key_never_reach_the_database_the_log_or_an_argument()
    {
        var guest = new FakeBackupGuest { Provision = _ => (1, ["boom"]) };
        var processes = new ProvisionDurabilityTests.GuestProcesses(guest);
        var h = await HostAsync(guest, runner: new SystemSshRunner(processes));

        var first = await ReinstallAsync(h, "reinstall-1");
        var backupId = Result<ReinstallJobResult>(first).BackupId!;
        // The key a failed reinstall keeps for its retry, in memory.
        var keys = h.App.Service<ReinstallKeys>();
        var privatePem = keys.Take(ProvisionHarness.Vm, backupId)!;
        Assert.NotNull(privatePem);
        keys.Hold(ProvisionHarness.Vm, backupId, privatePem);
        guest.Provision = _ => (0, ["echo " + guest.Made[backupId].Key, .. FakeSshRunner.Clean]);
        var retry = await ReinstallAsync(h, "reinstall-2", desired: false);
        Assert.True(retry.State == JobState.Succeeded, retry.Error);
        Assert.Null(keys.Take(ProvisionHarness.Vm, backupId));
        await h.App.DisposeAsync();

        var e = guest.Made[backupId].Key;
        var body = privatePem.Split('\n').Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)).ToArray();
        string[] forms = [e, Convert.ToBase64String(Encoding.UTF8.GetBytes(e)), "PRIVATE KEY", "AGE-SECRET-KEY", .. body];
        var database = string.Concat(Directory.GetFiles(_root, "constructd.db*").Select(f => Encoding.UTF8.GetString(File.ReadAllBytes(f))));
        Assert.Contains(backupId, database, StringComparison.Ordinal);
        var logs = h.App.Logs.AllText();
        var argv = string.Join('\n', processes.Calls.SelectMany(c => c.Arguments));
        Assert.Contains("restore --from-host " + backupId, argv, StringComparison.Ordinal);
        foreach (var form in forms)
        {
            Assert.DoesNotContain(form, database, StringComparison.Ordinal);
            Assert.DoesNotContain(form, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(form, argv, StringComparison.Ordinal);
        }

        // E reached the guest, on stdin only.
        Assert.Contains(processes.Calls, c => c.StandardInput?.Contains(e, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_restore_job_restores_a_chosen_backup_with_the_devices_key()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        var id = await MakeBackupAsync(h, guest);

        var job = await RestoreAsync(h, "restore-1", id, guest.Made[id].Key);

        Assert.True(job.State == JobState.Succeeded, job.Error);
        Assert.Equal(id, Result<RestoreJobResult>(job).BackupId);
        Assert.Equal([id], guest.Restored);

        // A wrong key is the guest's decryption failure; the VM was not touched, so it may be run again.
        var wrong = await RestoreAsync(h, "restore-2", id, "AGE-SECRET-KEY-1WRONG");
        Assert.Equal("restore-failed", Result<RestoreJobResult>(wrong).Code);
        Assert.True((await RestoreAsync(h, "restore-3", id, guest.Made[id].Key)).State == JobState.Succeeded);
    }

    [Fact]
    public async Task A_restore_job_needs_the_key_and_a_backup_of_this_VM()
    {
        var guest = new FakeBackupGuest();
        var h = await HostAsync(guest);
        var noKey = await h.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs",
            new { kind = "restore", requestId = "r1", backupId = new string('a', 32) });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
        var unknown = await h.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs",
            new { kind = "restore", requestId = "r2", backupId = new string('a', 32), secrets = new { backupKey = "AGE-SECRET-KEY-1ABC" } });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    /// <summary>The guest backs itself up at the host's request, as a scheduled run would.</summary>
    private static async Task<string> MakeBackupAsync(ProvisionHarness h, FakeBackupGuest guest)
    {
        var coordinator = h.App.Service<Constructd.Api.Backups.BackupCoordinator>();
        await using (await coordinator.LockAsync("alice", ProvisionHarness.Vm, default))
            await coordinator.OpenRequestLockedAsync("alice", ProvisionHarness.Vm, BackupTriggers.Manual, default);
        var output = new List<string>();
        Assert.Equal(0, await guest.RunAsync(new SshTarget("guest", 22, "agent", null, null), BackupScript.CreatePreReinstall, null,
            new Collect(output), TimeSpan.FromMinutes(1), default));
        return BackupScript.CreatedId(output)!;
    }

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }

    // ── Appendix C.1: the engine killed in a step ──────────────────────────────────

    [Theory]
    [InlineData("backup-create", "backup")]
    [InlineData("create", "create")]
    [InlineData("provision", "provision")]
    [InlineData("restore", "restore")]
    public async Task A_reinstall_killed_in_a_step_is_retried_without_losing_the_backup_or_newer_data(string at, string phase)
    {
        var guest = new FakeBackupGuest();
        var first = await HostAsync(guest);
        if (at == "create") first.App.Driver.HoldCreate = true;
        else guest.HoldAt = at;
        var jobId = await StartReinstallAsync(first, "reinstall-1");
        if (at == "create")
        {
            await WaitUntilAsync(() => first.App.Driver.Calls.Count(c => c.StartsWith("create:", StringComparison.Ordinal)) == 2);
        }
        else
        {
            await guest.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        var madeBefore = guest.Made.Keys.ToList();
        // The kill: the host goes away in the middle of the step.
        await first.App.DisposeAsync();

        var second = await HostAsync(guest, createVm: false);
        var survived = at == "backup-create";
        if (survived)
        {
            // The fake hypervisor's memory went with the first host; the VM was never deleted.
            second.App.Driver.SetState(ProvisionHarness.Vm, VmState.Running);
            guest.Adopt();
        }

        var interrupted = await second.Client.WaitForJobAsync(jobId);
        Assert.Equal(JobState.Failed, interrupted.State);
        Assert.Equal(phase, interrupted.Phase);

        var retry = await ReinstallAsync(second, "reinstall-2", desired: false);
        var result = Result<ReinstallJobResult>(retry);
        var backups = await BackupsAsync(second);
        switch (at)
        {
            case "backup-create":
                // Before the delete: the retry removes the dead attempt's key, backs up again and finishes.
                Assert.True(retry.State == JobState.Succeeded, retry.Error);
                Assert.Empty(madeBefore);
                Assert.Equal(result.BackupId, Assert.Single(backups).Id);
                Assert.Equal([result.BackupId!], guest.Restored);
                break;
            case "create" or "provision":
                // After the delete, with the key gone with the engine: the VM is back and provisioned, the
                // original backup is untouched and waits for a device's restore job.
                Assert.Equal(JobState.Failed, retry.State);
                Assert.Equal("restore-needs-device", result.Code);
                var id = Assert.Single(madeBefore);
                Assert.Equal(id, result.BackupId);
                Assert.Equal(id, Assert.Single(backups).Id);
                Assert.True(backups[0].IsPinned(second.App.Clock.UtcNow));
                Assert.Empty(guest.Restored);
                Assert.Contains(second.App.Driver.Calls, c => c == "create:" + ProvisionHarness.Vm);
                var restored = await RestoreAsync(second, "restore-1", id, guest.Made[id].Key);
                Assert.True(restored.State == JobState.Succeeded, restored.Error);
                Assert.Equal([id], guest.Restored);
                break;
            case "restore":
                // The restore may have changed the new VM: the retry neither deletes it nor restores again.
                Assert.Equal(JobState.Failed, retry.State);
                Assert.Equal("restore-already-ran", result.Code);
                Assert.Equal(Assert.Single(madeBefore), Assert.Single(backups).Id);
                Assert.DoesNotContain(second.App.Driver.Calls, c => c.StartsWith("create:", StringComparison.Ordinal) || c.StartsWith("remove:", StringComparison.Ordinal));
                Assert.Empty(guest.Restored);
                break;
        }

        // No job's key stays on the VM.
        Assert.DoesNotContain(guest.Recipients, r => r.Value.Label == BackupJobKey.Label);
    }

    [Fact]
    public async Task A_restore_killed_in_the_middle_is_not_run_again_over_the_VM()
    {
        var guest = new FakeBackupGuest();
        var first = await HostAsync(guest);
        var id = await MakeBackupAsync(first, guest);
        guest.HoldAt = "restore";
        var jobId = await StartRestoreAsync(first, "restore-1", id, guest.Made[id].Key);
        await guest.Held.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await first.App.DisposeAsync();

        var second = await HostAsync(guest, createVm: false);
        second.App.Driver.SetState(ProvisionHarness.Vm, VmState.Running);
        guest.Adopt();
        var interrupted = await second.Client.WaitForJobAsync(jobId);
        Assert.Equal(JobState.Failed, interrupted.State);
        Assert.Equal("restore", interrupted.Phase);

        var retry = await RestoreAsync(second, "restore-2", id, guest.Made[id].Key);
        Assert.Equal(JobState.Failed, retry.State);
        Assert.Equal("restore-already-ran", Result<RestoreJobResult>(retry).Code);
        Assert.Empty(guest.Restored);
        Assert.Equal(id, Assert.Single(await BackupsAsync(second)).Id);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var h in _hosts)
        {
            try { await h.DisposeAsync(); } catch (ObjectDisposedException) { }
        }

        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
