using System.Text;
using System.Text.Json;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Provisioning;

/// <summary>The engine's side of <c>bin/construct-repair-agent.sh</c>: commands, the job's prompt, output lines.</summary>
public sealed class RepairScriptTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void The_start_command_carries_only_the_checked_id_and_the_base64_prompt()
    {
        var prompt = "it's \"quoted\" $(rm -rf /) `x`\nline";
        var command = RepairScript.StartCommand("abc123", prompt, connectedAsRoot: false);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt));
        Assert.Equal($"sudo -n bash -lc 'bash /opt/construct/repo/bin/construct-repair-agent.sh --session abc123 --prompt-b64 {b64}' 2>&1", command);
        Assert.Equal("bash -lc 'bash /opt/construct/repo/bin/construct-repair-agent.sh --stop --session abc123' 2>&1", RepairScript.StopCommand("abc123", true));
        Assert.Throws<ArgumentException>(() => RepairScript.StartCommand("a;b", "x", true));
        Assert.Throws<ArgumentException>(() => RepairScript.StopCommand("A B", true));
    }

    [Fact]
    public void A_repair_in_t3_code_asks_the_script_for_a_thread_named_after_the_job_kind()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("p"));
        Assert.Equal($"bash -lc 'bash /opt/construct/repo/bin/construct-repair-agent.sh --session abc123 --prompt-b64 {b64} --via t3 --kind provision' 2>&1",
            RepairScript.StartCommand("abc123", "p", connectedAsRoot: true, inT3: true, kind: JobKinds.Provision));
        // The chat in Construct starts the script as before, which every guest's script knows.
        Assert.Equal($"bash -lc 'bash /opt/construct/repo/bin/construct-repair-agent.sh --session abc123 --prompt-b64 {b64}' 2>&1",
            RepairScript.StartCommand("abc123", "p", connectedAsRoot: true, inT3: false, kind: JobKinds.Provision));
        Assert.Throws<ArgumentException>(() => RepairScript.StartCommand("abc123", "p", true, inT3: true, kind: "x y"));
    }

    [Fact]
    public void The_start_line_of_a_t3_code_thread_names_the_thread_and_its_environment()
    {
        var info = RepairScript.AgentInfo("""{"type":"start","agent":"t3","via":"t3","thread":"thread:9f2%3A","environment":"6f1c0b9e-2d4a-4c55-9a41-0e2b7c1d5f60","title":"Construct repair: provision failed"}""");
        Assert.Equal(new RepairT3Thread("6f1c0b9e-2d4a-4c55-9a41-0e2b7c1d5f60", "thread:9f2%3A", "Construct repair: provision failed"), info!.T3Thread);
        Assert.Equal(("t3", "t3"), (info.Agent, info.Via));
        // No environment: the thread is still named, T3 Code then opens without a link to it.
        Assert.Equal(new RepairT3Thread(null, "t1", null), RepairScript.AgentInfo("""{"type":"start","agent":"t3","via":"t3","thread":"t1","environment":""}""")!.T3Thread);
        // Ids that would leave a link's path segment are not taken.
        Assert.Null(RepairScript.AgentInfo("""{"type":"start","agent":"t3","via":"t3","thread":"a/../b","environment":"e"}""")!.T3Thread);
        Assert.Null(RepairScript.AgentInfo("""{"type":"start","agent":"t3","via":"t3","thread":"t1","environment":"e f"}""")!.T3Thread);
        Assert.Null(RepairScript.AgentInfo("""{"type":"start","agent":"claude","via":"direct"}""")!.T3Thread);
    }

    [Fact]
    public void A_reply_is_one_json_line_whatever_it_holds()
    {
        var line = RepairScript.ReplyLine("two\nlines \"and\" 'quotes'");
        Assert.DoesNotContain('\n', line);
        Assert.Equal("two\nlines \"and\" 'quotes'", JsonDocument.Parse(line).RootElement.GetProperty("reply").GetString());
    }

    [Fact]
    public void Only_failures_in_the_guest_get_a_prompt()
    {
        var log = new[] { new JobProgressLine(DateTimeOffset.UnixEpoch, "provision: failed") };
        var provision = Json(new { fixPrompt = new[] { "On the last run:", "- Step 'X' failed (exit 2); log: /l.log" }, rebooted = true });
        Assert.StartsWith("On the last run:\n- Step 'X' failed", RepairScript.JobPrompt(JobKinds.Provision, provision, log), StringComparison.Ordinal);
        Assert.True(RepairScript.Rebooted(provision));
        Assert.Null(RepairScript.JobPrompt(JobKinds.Provision, Json(new { fixPrompt = Array.Empty<string>() }), log));
        Assert.Null(RepairScript.JobPrompt(JobKinds.Provision, null, log));

        var reinstall = Json(new { provision = new { fixPrompt = new[] { "On the last run:" }, rebooted = false } });
        Assert.Contains("builds this VM anew", RepairScript.JobPrompt(JobKinds.Reinstall, reinstall, log), StringComparison.Ordinal);
        Assert.False(RepairScript.Rebooted(reinstall));
        Assert.Null(RepairScript.JobPrompt(JobKinds.Reinstall, Json(new { failedStep = "backup" }), log));

        var restore = Json(new { backupId = "b1", failedStep = "restore", code = "restore-failed", message = "exit 5" });
        Assert.Contains("Restoring backup b1 into this VM failed: exit 5", RepairScript.JobPrompt(JobKinds.Restore, restore, log), StringComparison.Ordinal);
        Assert.Null(RepairScript.JobPrompt(JobKinds.Restore, Json(new { failedStep = "restore", code = "secret-missing" }), log));
        Assert.Null(RepairScript.JobPrompt(JobKinds.CreateVm, provision, log));
    }

    [Fact]
    public void A_job_with_warnings_gets_a_prompt_that_says_it_finished_and_that_a_provision_follows_the_fix()
    {
        var log = new[] { new JobProgressLine(DateTimeOffset.UnixEpoch, "provision: done") };
        var provision = Json(new { succeeded = true, fixPrompt = new[] { "On the last run:", "- Step 'X' failed (exit 2); log: /l.log" } });
        var text = RepairScript.JobPrompt(JobKinds.Provision, provision, log, warned: true)!;
        Assert.StartsWith("The provision of this VM finished; only the optional steps below failed.\nOn the last run:\n- Step 'X' failed", text, StringComparison.Ordinal);
        Assert.Contains("When the cause is fixed, the engine provisions this VM again.", text, StringComparison.Ordinal);
        Assert.Null(RepairScript.JobPrompt(JobKinds.Provision, Json(new { succeeded = true, fixPrompt = Array.Empty<string>() }), log, warned: true));

        var reinstall = RepairScript.JobPrompt(JobKinds.Reinstall, Json(new { succeeded = true, provision = new { fixPrompt = new[] { "On the last run:" } } }), log, warned: true)!;
        Assert.StartsWith("This VM was just reinstalled from a fresh image; only the optional steps below failed.", reinstall, StringComparison.Ordinal);
        Assert.Contains("it does not reinstall it, so a fix inside this VM stays", reinstall, StringComparison.Ordinal);
        Assert.DoesNotContain("builds this VM anew", reinstall, StringComparison.Ordinal);

        // The T3 Code thread is named after the warnings, not a failure.
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("p"));
        Assert.Equal($"bash -lc 'bash /opt/construct/repo/bin/construct-repair-agent.sh --session abc123 --prompt-b64 {b64} --via t3 --kind reinstall --warned' 2>&1",
            RepairScript.StartCommand("abc123", "p", connectedAsRoot: true, inT3: true, kind: JobKinds.Reinstall, warned: true));
        Assert.DoesNotContain("--warned", RepairScript.StartCommand("abc123", "p", connectedAsRoot: true, inT3: false, kind: JobKinds.Reinstall, warned: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Output_lines_are_parsed_and_capped()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.Equal(new RepairLine(3, at, "turn", null, "question"), RepairScript.Parse("{\"type\":\"turn\",\"outcome\":\"question\"}", 3, at));
        Assert.Equal(new RepairLine(4, at, "log", "sudo: a password is required"), RepairScript.Parse("sudo: a password is required", 4, at));
        Assert.Equal("log", RepairScript.Parse("{broken", 5, at).Kind);
        var big = RepairScript.Parse(JsonSerializer.Serialize(new { type = "text", text = new string('x', 100_000) }), 6, at);
        Assert.Equal(RepairScript.MaxText + 3, big.Text!.Length);
        Assert.Equal(new RepairAgentInfo("codex", "direct"), RepairScript.AgentInfo("{\"type\":\"start\",\"agent\":\"codex\",\"via\":\"direct\"}"));
    }
}
