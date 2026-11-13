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
