using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Repair;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary>The repair agent (design 11.5, S26) with the scripted guest of fake mode and the fake SSH runner.</summary>
public sealed class RepairAgentTests
{
    private const string Log = "/var/log/construct/provision/step-0-Installing-SDKs.log";

    public RepairAgentTests() => RepairSessions.RebootProbeDelay = TimeSpan.FromMilliseconds(20);

    private static FakeSshRunner Failing() => new() { Provision = _ => (3, FakeSshRunner.WithErrors(("Installing SDKs", 2, Log))) };

    private static FakeGuestSessionRunner Guests(ProvisionHarness h) => h.App.Service<FakeGuestSessionRunner>();

    private static async Task<T> Until<T>(Func<Task<T?>> probe, string what, int seconds = 20) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for " + what);
    }

    private static async Task<RepairSessionResponse?> RepairOrNull(HttpClient client, string vm = ProvisionHarness.Vm, long after = 0)
    {
        var response = await client.GetAsync($"/api/v1/vms/{vm}/repair?after={after}");
        return response.StatusCode == HttpStatusCode.OK ? await response.ReadAsync<RepairSessionResponse>() : null;
    }

    private static Task<RepairSessionResponse> InState(HttpClient client, string state) =>
        Until(async () => await RepairOrNull(client) is { } s && s.State == state ? s : null, "repair " + state);

    /// <summary>A failed provision with a failed step, and the repair it started, waiting for a reply.</summary>
    private static async Task<(ProvisionHarness H, JobResponse Failed, RepairSessionResponse Session)> FailedAsync(FakeSshRunner? ssh = null, string? databasePath = null)
    {
        ssh ??= Failing();
        var h = await ProvisionHarness.StartAsync(ssh, databasePath);
        JobRetryTests.Installed(h);
        var failed = await h.RunAsync("click-1");
        Assert.Equal(JobState.Failed, failed.State);
        var session = await InState(h.Client, "waiting");
        return (h, failed, session);
    }

    [Fact]
    public async Task A_failed_provision_starts_the_repair_agent_on_the_vm_with_the_fix_prompt()
    {
        var (h, failed, session) = await FailedAsync();
        await using var _ = h;
        Assert.Equal(failed.Id, session.JobId);
        Assert.Equal(JobKinds.Provision, session.JobKind);
        Assert.True(session.Automatic);
        Assert.Equal("claude", session.Agent);
        Assert.Equal("direct", session.Via);
        Assert.Contains(session.Lines, l => l.Kind == "tool" && l.Text!.StartsWith("Bash: tail", StringComparison.Ordinal));
        Assert.Contains(session.Lines, l => l is { Kind: "turn", Outcome: "question" });

        var guest = Assert.Single(Guests(h).Sessions);
        // As root through sudo -n: stdin is the conversation, so no password goes there.
        Assert.StartsWith("sudo -n bash -lc 'bash /opt/construct/repo/bin/construct-repair-agent.sh --session " + session.Id + " --prompt-b64 ", guest.Command, StringComparison.Ordinal);
        var b64 = guest.Command.Split("--prompt-b64 ")[1].Split('\'')[0];
        var prompt = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Contains("Step 'Installing SDKs' failed (exit 2); log: " + Log, prompt, StringComparison.Ordinal);
        Assert.Contains("The end of the provision job's log in the engine:", prompt, StringComparison.Ordinal);
        // The failed run rebooted the VM (bootstrap path): the repair waited for SSH first.
        Assert.Contains(h.Ssh.Commands, c => c == "true");
    }

    [Fact]
    public async Task Output_is_read_by_sequence_and_announced_on_the_change_stream()
    {
        await using var h = await ProvisionHarness.StartAsync(Failing());
        JobRetryTests.Installed(h);
        using var changes = h.App.Service<ChangeFeed>().Subscribe(null);
        await h.RunAsync("click-1");
        var session = await InState(h.Client, "waiting");

        var last = session.Lines[^1].Seq;
        Assert.Empty((await RepairOrNull(h.Client, after: last))!.Lines);
        Assert.Equal(session.Lines.Skip(1).Select(l => l.Seq), (await RepairOrNull(h.Client, after: session.Lines[0].Seq))!.Lines.Select(l => l.Seq));
        var byId = await (await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}")).ReadAsync<RepairSessionResponse>();
        Assert.Equal(session.Id, byId.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}")).StatusCode);
        var seen = new List<Change>();
        await Until(async () =>
        {
            while (changes.Live.TryRead(out var change)) seen.Add(change);
            await Task.Yield();
            return seen.Any(c => c.Type == "repair.ended") ? seen : null;
        }, "repair.ended");
        var repair = seen.Where(c => c.Type.StartsWith("repair.", StringComparison.Ordinal)).ToList();
        Assert.All(repair, c => Assert.Equal((session.Id, ProvisionHarness.Vm, "alice"), (c.Id, c.Vm, c.Owner)));
        Assert.All(repair, c => Assert.Null(c.Initiator));
        Assert.Equal("repair.started", repair[0].Type);
        Assert.Contains(repair, c => c.Type == "repair.output");
        Assert.Single(repair, c => c.Type == "repair.ended");
    }

    [Fact]
    public async Task A_reply_reaches_the_agent_on_stdin_and_a_fix_retries_the_job_once()
    {
        var (h, failed, session) = await FailedAsync();
        await using var _ = h;
        h.Ssh.Provision = _ => (0, FakeSshRunner.Clean);
        const string reply = "Take option A; it's \"fine\" & $(touch /tmp/pwned)\nsecond line";
        var response = await h.Client.PostAsJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}/reply", new { text = reply });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var guest = Assert.Single(Guests(h).Sessions);
        var line = Assert.Single(guest.Received);
        Assert.Equal(reply, JsonDocument.Parse(line).RootElement.GetProperty("reply").GetString());
        Assert.DoesNotContain(h.Ssh.Commands.Append(guest.Command), c => c.Contains("option A", StringComparison.Ordinal));

        var ended = await InState(h.Client, "ended");
        Assert.Equal("fixed", ended.Outcome);
        Assert.Contains(ended.Lines, l => l.Kind == "reply" && l.Text == reply);
        Assert.NotNull(ended.RetryJobId);
        var retry = await h.Client.WaitForJobAsync(ended.RetryJobId!);
        Assert.Equal(JobState.Succeeded, retry.State);
        var stored = (await h.App.Service<Constructd.Core.Abstractions.IJobQueryStore>().ListAsync(default)).Single(j => j.Id == retry.Id);
        Assert.Equal("retry:" + failed.Id, stored.OperationKey);
    }

    [Fact]
    public async Task A_retry_that_fails_again_starts_no_second_repair()
    {
        var (h, _, session) = await FailedAsync();
        await using var __ = h;
        // The retry comes later than the failed job (the test clock stands still otherwise).
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        (await h.Client.PostAsJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}/reply", new { text = "A" })).EnsureSuccessStatusCode();
        var ended = await InState(h.Client, "ended");
        var retry = await h.Client.WaitForJobAsync(ended.RetryJobId!);
        Assert.Equal(JobState.Failed, retry.State);

        await Task.Delay(500);
        Assert.Single(Guests(h).Sessions);
        Assert.Equal(session.Id, (await RepairOrNull(h.Client))!.Id);
        // By hand it may still be repaired.
        var manual = await h.Client.PostAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair", null);
        Assert.Equal(HttpStatusCode.Accepted, manual.StatusCode);
        var again = await manual.ReadAsync<RepairSessionResponse>();
        Assert.Equal(retry.Id, again.JobId);
        Assert.False(again.Automatic);
    }

    [Fact]
    public async Task A_new_job_on_the_vm_ends_the_repair_before_it_runs()
    {
        var (h, _, session) = await FailedAsync();
        await using var _ = h;
        h.Ssh.Provision = _ => (0, FakeSshRunner.Clean);
        var before = h.Ssh.Calls.Count;
        var job = await h.RunAsync("click-2");
        Assert.Equal(JobState.Succeeded, job.State);

        var ended = await InState(h.Client, "ended");
        Assert.Equal("superseded", ended.Outcome);
        Assert.Null(ended.RetryJobId);
        Assert.True(Assert.Single(Guests(h).Sessions).Disposed);
        // The agent's processes were stopped on the VM before the new job's first command.
        var after = h.Ssh.Commands.Skip(before).ToList();
        var stop = after.FindIndex(c => c.Contains("construct-repair-agent.sh --stop --session " + session.Id, StringComparison.Ordinal));
        Assert.True(stop >= 0, "no stop command");
        Assert.True(stop < after.FindIndex(c => c.Contains("bin/provision.sh", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.Conflict,
            (await h.Client.PostAsJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}/reply", new { text = "late" })).StatusCode);
    }

    [Fact]
    public async Task Ending_a_session_stops_the_agent_and_refuses_later_replies()
    {
        var (h, _, session) = await FailedAsync();
        await using var _ = h;
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}")).StatusCode);
        var ended = await InState(h.Client, "ended");
        Assert.Equal("stopped", ended.Outcome);
        Assert.Null(ended.RetryJobId);
        Assert.True(Assert.Single(Guests(h).Sessions).Disposed);
        Assert.Contains(h.Ssh.Commands, c => c.Contains("construct-repair-agent.sh --stop --session " + session.Id, StringComparison.Ordinal));
        var late = await h.Client.PostAsJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}/reply", new { text = "late" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("repair-ended", await late.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}")).StatusCode);
    }

    [Fact]
    public async Task Only_the_owner_and_admins_read_or_steer_a_repair()
    {
        var (h, _, session) = await FailedAsync();
        await using var _ = h;
        var url = $"/api/v1/vms/{ProvisionHarness.Vm}/repair";
        var bob = await h.App.CreateUserClientAsync("bob");
        var vmToken = h.App.CreateVmTokenClient(h.VmToken!);
        foreach (var other in new[] { bob, vmToken })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"{url}/{session.Id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsync(url, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.PostAsJsonAsync($"{url}/{session.Id}/reply", new { text = "x" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"{url}/{session.Id}")).StatusCode);
        }

        Assert.Empty(Assert.Single(Guests(h).Sessions).Received);
        Assert.Equal("waiting", (await RepairOrNull(h.Client))!.State);
        var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        Assert.Equal(session.Id, (await RepairOrNull(admin))!.Id);
    }

    [Fact]
    public async Task A_reply_names_the_session_of_its_own_vm_only()
    {
        var (h, _, session) = await FailedAsync();
        await using var _ = h;
        await h.Client.CreateVmAsync("other");
        var response = await h.Client.PostAsJsonAsync($"/api/v1/vms/other/repair/{session.Id}/reply", new { text = "x" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.DeleteAsync($"/api/v1/vms/other/repair/{session.Id}")).StatusCode);
        Assert.Empty(Assert.Single(Guests(h).Sessions).Received);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await h.Client.PostAsJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}/reply", new { text = "  " })).StatusCode);
    }

    [Fact]
    public async Task With_automatic_repair_off_a_failure_starts_none_and_a_request_does()
    {
        await using var h = await ProvisionHarness.StartAsync(Failing());
        JobRetryTests.Installed(h);
        var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        (await admin.PutAsJsonAsync("/api/v1/host/config", new { repair = new { auto = false } })).EnsureSuccessStatusCode();
        var failed = await h.RunAsync("click-1");
        await Task.Delay(500);
        Assert.Empty(Guests(h).Sessions);
        var none = await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Contains("no-repair", await none.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var started = await h.Client.PostAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal(failed.Id, (await started.ReadAsync<RepairSessionResponse>()).JobId);
        var twice = await h.Client.PostAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair", null);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("repair-running", await twice.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_outside_the_guest_steps_is_not_repaired()
    {
        await using var h = await ProvisionHarness.StartAsync(new FakeSshRunner { UnpackExit = 2 });
        JobRetryTests.Installed(h);
        Assert.Equal(JobState.Failed, (await h.RunAsync("click-1")).State);
        await Task.Delay(300);
        Assert.Empty(Guests(h).Sessions);
        var response = await h.Client.PostAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not-repairable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_agents_output_reaches_neither_the_database_nor_the_log()
    {
        var path = Path.Combine(Path.GetTempPath(), "repair-" + Guid.NewGuid().ToString("n") + ".db");
        try
        {
            const string secret = "ghp_REPAIRSECRET0123456789";
            ProvisionHarness? harness = null;
            try
            {
                harness = await ProvisionHarness.StartAsync(Failing(), path);
                JobRetryTests.Installed(harness);
                Guests(harness).Script = async s =>
                {
                    s.Emit(new { type = "start", agent = "claude", via = "direct" });
                    s.Emit(new { type = "text", text = "Found the token " + secret + " in the env." });
                    s.EmitRaw("not json: " + secret);
                    s.Emit(new { type = "turn", outcome = "question" });
                    await s.ReadLineAsync();
                };
                await harness.RunAsync("click-1");
                var session = await InState(harness.Client, "waiting");
                Assert.Equal(2, session.Lines.Count(l => l.Text?.Contains(secret, StringComparison.Ordinal) == true));
                (await harness.Client.PostAsJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}/reply", new { text = "reply " + secret })).EnsureSuccessStatusCode();
                await harness.Client.DeleteAsync($"/api/v1/vms/{ProvisionHarness.Vm}/repair/{session.Id}");
                await InState(harness.Client, "ended");
                Assert.DoesNotContain(secret, harness.App.Logs.AllText(), StringComparison.Ordinal);
            }
            finally
            {
                if (harness is not null) await harness.DisposeAsync();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                Assert.DoesNotContain(secret, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*")) File.Delete(file);
        }
    }
}
