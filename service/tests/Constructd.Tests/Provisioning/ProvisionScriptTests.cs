using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;
using Constructd.Fakes;

namespace Constructd.Tests.Provisioning;

/// <summary>The pure parts: result block, fix prompt, marker file, desired hash, the ssh argument vectors.</summary>
public sealed class ProvisionScriptTests
{
    [Fact]
    public void The_result_parser_takes_the_last_complete_block_and_ignores_colour()
    {
        var parsed = ProvisionScript.ParseResult([
            "===CONSTRUCT-PROVISION-RESULT===", "errors=0", "===END-CONSTRUCT-PROVISION-RESULT===",
            "\x1b[32mnoise\x1b[0m",
            "\x1b[0m===CONSTRUCT-PROVISION-RESULT===\r", "errors=2", "error=Installing SDKs|2|/var/log/a.log", "error=Old step|5",
            "===END-CONSTRUCT-PROVISION-RESULT===",
        ]);
        Assert.True(parsed.IsValid);
        Assert.Equal(2, parsed.ErrorCount);
        Assert.Equal([new ProvisionStepError("Installing SDKs", 2, "/var/log/a.log"), new ProvisionStepError("Old step", 5, "")], parsed.Errors);

        Assert.False(ProvisionScript.ParseResult(["===CONSTRUCT-PROVISION-RESULT===", "errors=1", "===END-CONSTRUCT-PROVISION-RESULT==="]).IsValid);
        Assert.False(ProvisionScript.ParseResult(["===CONSTRUCT-PROVISION-RESULT===", "errors=0"]).Found);
    }

    [Fact]
    public void The_fix_prompt_is_the_PowerShell_one()
    {
        Assert.Empty(ProvisionScript.FixPrompt([]));
        Assert.Equal([
            "On the last Construct provisioning run, the following step(s) failed:",
            "- Step 'A' failed (exit 1); log: /l/a.log",
            "- Step 'B' failed (exit 2); log: /l/b.log",
            "Read the provisioning logs at: /l/a.log, /l/b.log and diagnose and fix the underlying problem.",
        ], ProvisionScript.FixPrompt([new("A", 1, "/l/a.log"), new("B", 2, "/l/b.log")]));
        Assert.Equal("Check the provisioning output above and diagnose and fix the underlying problem.",
            ProvisionScript.FixPrompt([new("A", 1, "")])[^1]);
    }

    [Fact]
    public void The_marker_file_is_read_with_config_set_quoting()
    {
        var marker = ProvisionScript.ParseMarker("INSTALLED_AT=2026-01-01T00:00:00Z\nCONSTRUCT_COMMIT='abc1234'\n# note\n\nCONSTRUCT_CHANNEL=main\n");
        Assert.Equal("abc1234", marker["CONSTRUCT_COMMIT"]);
        Assert.Equal("main", marker["CONSTRUCT_CHANNEL"]);
        Assert.Equal(3, marker.Count);
    }

    [Fact]
    public void The_desired_hash_is_stable_and_changes_with_any_setting()
    {
        var a = new VmDesiredState(["default"], new ProvisionSettings { T3Code = "true" });
        Assert.Equal(ProvisionScript.DesiredHash(a), ProvisionScript.DesiredHash(a with { Projects = ["default"] }));
        Assert.NotEqual(ProvisionScript.DesiredHash(a), ProvisionScript.DesiredHash(a with { Settings = new ProvisionSettings { T3Code = "false" } }));
        Assert.NotEqual(ProvisionScript.DesiredHash(a), ProvisionScript.DesiredHash(a with { Projects = ["default", "web"] }));
        Assert.Matches("^[0-9a-f]{64}$", ProvisionScript.DesiredHash(a));
    }

    [Fact]
    public void Root_commands_are_wrapped_as_Invoke_SshStream_does()
    {
        Assert.Equal("env TERM=xterm-256color FORCE_COLOR=1 CLICOLOR_FORCE=1 DEBIAN_FRONTEND=noninteractive bash -lc 'echo '\\''hi'\\''' 2>&1",
            ProvisionScript.AsRoot("echo 'hi'", connectedAsRoot: true, sudoPasswordOnStdin: false));
        Assert.StartsWith("sudo -S -p '' env TERM=", ProvisionScript.AsRoot("x", false, true), StringComparison.Ordinal);
        Assert.StartsWith("sudo -n env TERM=", ProvisionScript.AsRoot("x", false, false), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ssh_runner_passes_stdin_and_never_puts_it_in_an_argument()
    {
        var processes = new RecordingProcessRunner();
        processes.Respond(new ProcessResult(0, "a\nb", "", false));
        var runner = new SystemSshRunner(processes);
        var lines = new List<string>();
        var target = new SshTarget("guest.test", 2222, "agent", "/keys/id", "/data/known_hosts");
        var exit = await runner.RunAsync(target, "cat > /tmp/x", "secret-value", new Collect(lines), TimeSpan.FromMinutes(1), default);

        Assert.Equal(0, exit);
        Assert.Equal(["a", "b"], lines);
        var call = Assert.Single(processes.Calls);
        Assert.Equal("ssh", call.FileName);
        Assert.Equal(["-i", "/keys/id", "-o", "IdentitiesOnly=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=/data/known_hosts",
            "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4",
            "-p", "2222", "agent@guest.test", "cat > /tmp/x"], call.Arguments);
        Assert.Equal("secret-value", call.StandardInput);
        Assert.DoesNotContain(call.Arguments, a => a.Contains("secret-value", StringComparison.Ordinal));

        Assert.Equal(["-q", "-o", "StrictHostKeyChecking=accept-new", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=4", "-P", "2222", "/tmp/a.tgz", "agent@[fd00::1]:/tmp/b.tgz"],
            SystemSshRunner.ScpArguments(new SshTarget("fd00::1", 2222, "agent", null, null), "/tmp/a.tgz", "/tmp/b.tgz"));
    }

    [Fact]
    public void The_secret_bag_masks_raw_base64_and_decoded_forms()
    {
        var bag = new JobSecretBag();
        var creds = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("https://bob:tok%2Fen-1234@git.test\nhttps://amy:second-5678@other.test"));
        bag.Set("c", creds);
        bag.Set("p", "hunter2-password", base64: false);
        var text = $"{creds} https://bob:tok%2Fen-1234@git.test tok/en-1234 second-5678 hunter2-password {Convert.ToBase64String("hunter2-password"u8.ToArray())}";
        Assert.Equal(string.Join(' ', Enumerable.Repeat(JobSecretBag.Mask, 6)), bag.Redact(text));
        bag.Clear();
        Assert.Equal("hunter2-password", bag.Redact("hunter2-password"));
    }

    [Fact]
    public void Guest_generated_secrets_are_dropped_from_the_output()
    {
        var filter = new GuestOutputFilter();
        var shown = new[] { "before", "-----BEGIN OPENSSH PRIVATE KEY-----", "b3BlbnNzaC1rZXk", "-----END OPENSSH PRIVATE KEY-----", "  Open:      http://vm:8000/?tkn=abc123def", "after" }
            .Select(filter.Pass).Select(l => l ?? "(dropped)").ToArray();
        Assert.Equal(["before", "[private key not shown]", "(dropped)", "(dropped)", "  Open:      http://vm:8000/?tkn=[secret]", "after"], shown);
        var bag = new JobSecretBag();
        bag.Set("t", "YWJj");
        Assert.Equal("tkn=[secret]", bag.Redact("tkn=abc"));
    }

    private sealed class Collect(List<string> lines) : IProgress<string>
    {
        public void Report(string value) => lines.Add(value);
    }
}
