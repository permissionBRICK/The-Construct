using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.HyperV;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Windows;

/// <summary>
/// What the Hyper-V driver actually runs. Every operation is asserted twice: the exact
/// <c>powershell.exe</c> argument vector, and the decoded script — which must go through
/// <c>drivers\Load-ConstructDriver.ps1</c> and the contract function of <c>docs/drivers.md</c>, never
/// a raw Hyper-V cmdlet. That is the property that keeps the service and the local install on one
/// implementation.
/// </summary>
public sealed class HyperVDriverTests
{
    private static readonly VmDescriptor Descriptor =
        new("work-vm", Cpu: 4, RamGb: 8, DiskGb: 100, IsoPath: @"C:\isos\work-vm-autoinstall.iso",
            Nested: true, AutomaticCheckpoints: false);

    [Fact]
    public async Task Every_operation_runs_powershell_with_the_same_fixed_switches()
    {
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")));

        await driver.StartAsync("work-vm", CancellationToken.None);

        var call = runner[0];
        Assert.Equal("powershell.exe", call.FileName);
        Assert.Equal(
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand"],
            call.Arguments.Take(5));

        // The script itself is one opaque argv element: base64 of UTF-16LE, so nothing in it can be
        // reinterpreted as another argument.
        Assert.Equal(6, call.Arguments.Count);
        Assert.Contains("Start-ConstructVm", Script(call));
    }

    [Fact]
    public async Task Every_script_dot_sources_the_shared_lib_and_the_driver_loader()
    {
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")));

        await driver.StopAsync("work-vm", CancellationToken.None);
        var script = Script(runner[0]);

        Assert.Contains(@"'C:\Construct\lib\AgentVm.Common.ps1'", script, StringComparison.Ordinal);
        Assert.Contains(@"'C:\Construct\drivers\Load-ConstructDriver.ps1'", script, StringComparison.Ordinal);
        Assert.Contains("-Backend 'hyperv-local'", script, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-Json $envelope -Compress", script, StringComparison.Ordinal);

        // No raw Hyper-V anywhere: that is the driver's job, on both entry points.
        Assert.DoesNotContain("Get-VM", script, StringComparison.Ordinal);
        Assert.DoesNotContain("New-VM", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_builds_the_descriptor_of_the_driver_contract_and_then_starts_the_vm()
    {
        var options = PlatformOptions.Create(o =>
        {
            o.SwitchName = "Construct Internal";
            o.VmStorageRoot = @"D:\VMs";
        });
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")), options);

        await driver.CreateVmAsync(Descriptor, progress: null, CancellationToken.None);
        var script = Script(runner[0]);

        Assert.Contains("$descriptor['Name'] = 'work-vm'", script, StringComparison.Ordinal);
        Assert.Contains("$descriptor['ProcessorCount'] = 4", script, StringComparison.Ordinal);
        Assert.Contains("$descriptor['MemoryGB'] = 8", script, StringComparison.Ordinal);
        Assert.Contains("$descriptor['DiskGB'] = 100", script, StringComparison.Ordinal);
        Assert.Contains("$descriptor['SwitchName'] = 'Construct Internal'", script, StringComparison.Ordinal);
        Assert.Contains(@"$descriptor['VhdPath'] = 'D:\VMs\work-vm.vhdx'", script, StringComparison.Ordinal);
        Assert.Contains(@"$descriptor['IsoPath'] = 'C:\isos\work-vm-autoinstall.iso'", script, StringComparison.Ordinal);
        Assert.Contains("$descriptor['Nested'] = $true", script, StringComparison.Ordinal);
        Assert.Contains("$descriptor['AutomaticCheckpoints'] = $false", script, StringComparison.Ordinal);
        Assert.Contains("New-ConstructVm -Descriptor $descriptor", script, StringComparison.Ordinal);

        // docs/drivers.md §3.3: New-ConstructVm leaves the VM off, so the caller starts it.
        Assert.True(
            script.IndexOf("New-ConstructVm", StringComparison.Ordinal) <
            script.IndexOf("Start-ConstructVm -Name 'work-vm'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_leaves_the_vhd_path_to_the_driver_when_no_storage_root_is_configured()
    {
        // Empty VmStorageRoot must mean "Hyper-V's own default folder" — the same disk location a
        // local install gets — not a path this service invents.
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")));

        await driver.CreateVmAsync(Descriptor, progress: null, CancellationToken.None);

        Assert.DoesNotContain("VhdPath", Script(runner[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_without_install_media_omits_the_iso()
    {
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")));

        await driver.CreateVmAsync(Descriptor with { IsoPath = null }, progress: null, CancellationToken.None);

        Assert.DoesNotContain("IsoPath", Script(runner[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("remove-vm", "Remove-ConstructVm -Name 'work-vm'")]
    [InlineData("start", "Start-ConstructVm -Name 'work-vm'")]
    [InlineData("stop", "Stop-ConstructVm -Name 'work-vm' -Force")]
    [InlineData("save", "Save-ConstructVm -Name 'work-vm'")]
    [InlineData("detach", "Detach-ConstructInstallMedia -Name 'work-vm'")]
    public async Task Each_operation_calls_its_contract_function(string operation, string expected)
    {
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")));

        var work = operation switch
        {
            "remove-vm" => driver.RemoveVmAsync("work-vm", null, CancellationToken.None),
            "start" => driver.StartAsync("work-vm", CancellationToken.None),
            "stop" => driver.StopAsync("work-vm", CancellationToken.None),
            "save" => driver.SaveAsync("work-vm", CancellationToken.None),
            _ => driver.DetachInstallMediaAsync("work-vm", CancellationToken.None),
        };
        await work;

        Assert.Contains(expected, Script(runner[0]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("running", VmState.Running)]
    [InlineData("off", VmState.Off)]
    [InlineData("paused", VmState.Paused)]
    [InlineData("saved", VmState.Saved)]
    [InlineData("absent", VmState.Absent)]
    [InlineData("unknown", VmState.Unknown)]
    [InlineData("Starting", VmState.Unknown)]
    [InlineData("", VmState.Unknown)]
    public async Task Every_contract_state_maps_onto_the_service_enum(string reported, VmState expected)
    {
        var (driver, _) = Driver(new RecordingProcessRunner().Respond(Ok($"\"{reported}\"")));

        Assert.Equal(expected, await driver.GetStateAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task An_unmapped_state_is_unknown_and_never_absent()
    {
        // docs/drivers.md §3.2: 'unknown' means "can't tell" and must never be read as "not
        // installed" — a mid-transition VM would otherwise look deletable.
        var (driver, _) = Driver(new RecordingProcessRunner().Respond(Ok("\"Saving\"")));

        Assert.Equal(VmState.Unknown, await driver.GetStateAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task The_endpoint_comes_from_the_contract_function()
    {
        var (driver, runner) = Driver(new RecordingProcessRunner()
            .Respond(Ok("""{"SshHost":"work-vm.mshome.net","SshPort":22}""")));

        var endpoint = await driver.GetEndpointAsync("work-vm", CancellationToken.None);

        Assert.Contains("Get-ConstructVmEndpoint -Name 'work-vm'", Script(runner[0]), StringComparison.Ordinal);
        Assert.Equal(new Endpoint("work-vm.mshome.net", 22), endpoint);
    }

    [Fact]
    public async Task An_endpointless_vm_reports_no_endpoint_rather_than_a_made_up_one()
    {
        var (driver, _) = Driver(new RecordingProcessRunner().Respond(Ok("""{"SshHost":"","SshPort":22}""")));

        Assert.Null(await driver.GetEndpointAsync("work-vm", CancellationToken.None));
    }

    [Fact]
    public async Task Wait_reachable_passes_the_timeout_and_returns_the_contract_answer()
    {
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("true")));

        var reachable = await driver.WaitReachableAsync(
            "work-vm", TimeSpan.FromMinutes(30), progress: null, CancellationToken.None);

        Assert.True(reachable);
        Assert.Contains(
            "Wait-ConstructVmReachable -Name 'work-vm' -TimeoutSeconds 1800",
            Script(runner[0]),
            StringComparison.Ordinal);

        // The process may outlive the wait itself (the driver settles afterwards), so its own timeout
        // has to be the wait plus a margin — otherwise the service kills a wait that is working.
        Assert.True(runner[0].Timeout > TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task An_expired_wait_is_reported_as_not_reachable_rather_than_as_a_failure()
    {
        // docs/drivers.md: an expired wait is non-fatal ($false), which the creation job turns into
        // its own VmNotReachableException.
        var (driver, _) = Driver(new RecordingProcessRunner().Respond(Ok("false")));

        Assert.False(await driver.WaitReachableAsync(
            "work-vm", TimeSpan.FromMinutes(1), progress: null, CancellationToken.None));
    }

    [Fact]
    public void Capabilities_come_from_the_contract_and_report_the_console_kind()
    {
        var (driver, runner) = Driver(new RecordingProcessRunner()
            .Respond(Ok("""{"Checkpoints":true,"Suspend":true,"Console":"vmconnect","Backend":"hyperv-local"}""")));

        var capabilities = driver.Capabilities;

        Assert.Contains("Get-ConstructDriverCapabilities", Script(runner[0]), StringComparison.Ordinal);
        Assert.True(capabilities.Checkpoints);
        Assert.True(capabilities.Suspend);
        Assert.Equal(ConsoleKind.VmConnect, capabilities.Console.Kind);
        Assert.Null(capabilities.Console.Url);

        // Read once and cached: the backend does not change while the service runs.
        _ = driver.Capabilities;
        Assert.Single(runner.Calls);
    }

    [Fact]
    public void A_url_console_keeps_its_url()
    {
        var (driver, _) = Driver(new RecordingProcessRunner()
            .Respond(Ok("""{"Checkpoints":false,"Suspend":true,"Console":"https://pve.example/?console=kvm"}""")));

        var console = driver.Capabilities.Console;

        Assert.Equal(ConsoleKind.Url, console.Kind);
        Assert.Equal("https://pve.example/?console=kvm", console.Url);
    }

    [Fact]
    public void A_failed_capability_probe_promises_nothing_and_is_retried()
    {
        var runner = new RecordingProcessRunner();
        runner.Default = new ProcessResult(1, string.Empty, "Hyper-V is not installed", TimedOut: false);
        var (driver, _) = Driver(runner);

        var capabilities = driver.Capabilities;

        // Reporting Suspend as available while the driver is unreachable would let the API accept a
        // save it cannot perform.
        Assert.False(capabilities.Suspend);
        Assert.False(capabilities.Checkpoints);
        Assert.Equal(ConsoleKind.None, capabilities.Console.Kind);

        _ = driver.Capabilities;
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task A_non_zero_exit_fails_with_a_message_that_carries_no_output()
    {
        using var logs = new LogSink();
        var runner = new RecordingProcessRunner().Respond(new ProcessResult(
            1,
            string.Empty,
            @"At C:\Construct\drivers\HyperVLocal.Driver.ps1:12 char:5 secret-detail",
            TimedOut: false));
        var (driver, _) = Driver(runner, logs: logs);

        var ex = await Assert.ThrowsAsync<HypervisorOperationException>(
            () => driver.StartAsync("work-vm", CancellationToken.None));

        Assert.Equal("The Hyper-V driver failed during 'start-vm' for VM 'work-vm'.", ex.Message);

        // The service composed this message itself, so it is safe to persist and show.
        Assert.Equal(ex.Message, SafeError.Describe(ex));

        // What an operator gets is how it failed, in our words — not PowerShell's text, which routinely
        // carries a script path or a whole command line.
        Assert.Equal("powershell.exe exited with 1", ex.Detail);
        Assert.DoesNotContain("secret-detail", logs.Text, StringComparison.Ordinal);
        Assert.Contains("start-vm", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_child_output_ever_reaches_a_log_entry()
    {
        // The driver is the one place in the service holding another process's stderr. The rule is the
        // same as everywhere else: dependency text is not repeated verbatim, the log included.
        using var logs = new LogSink();
        var runner = new RecordingProcessRunner()
            .Respond(new ProcessResult(0, "==> step SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false))
            .Respond(new ProcessResult(3, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: false))
            .Respond(new ProcessResult(0, """{"ok":false,"error":"SENTINEL-ERR"}""", "SENTINEL-ERR", TimedOut: false))
            .Respond(new ProcessResult(-1, "SENTINEL-OUT", "SENTINEL-ERR", TimedOut: true));
        var (driver, _) = Driver(runner, logs: logs);

        foreach (var attempt in Enumerable.Range(0, 4))
        {
            try
            {
                await driver.GetStateAsync("work-vm", CancellationToken.None);
            }
            catch (HypervisorOperationException)
            {
                // Each of the four failure shapes is expected to throw.
            }
        }

        Assert.DoesNotContain("SENTINEL-OUT", logs.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL-ERR", logs.Text, StringComparison.Ordinal);
        Assert.Contains("get-state", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_that_is_not_the_expected_envelope_fails_rather_than_being_guessed_at()
    {
        var runner = new RecordingProcessRunner().RespondStdout("==> Starting", "not json at all");
        var (driver, _) = Driver(runner);

        var ex = await Assert.ThrowsAsync<HypervisorOperationException>(
            () => driver.GetStateAsync("work-vm", CancellationToken.None));

        Assert.Equal("the last output line was not the expected JSON envelope", ex.Detail);
    }

    [Fact]
    public async Task An_error_reported_by_the_driver_fails_the_operation()
    {
        var runner = new RecordingProcessRunner()
            .RespondStdout("""{"ok":false,"error":"Hyper-V was unable to find a virtual machine"}""");
        var (driver, _) = Driver(runner);

        var ex = await Assert.ThrowsAsync<HypervisorOperationException>(
            () => driver.SaveAsync("work-vm", CancellationToken.None));

        Assert.Equal("save-vm", ex.Operation);

        // Not even the detail repeats the driver's own error text: it can name a VM path or the
        // arguments a cmdlet was called with.
        Assert.DoesNotContain("unable to find", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unable to find", ex.Detail);
        Assert.Equal("the driver reported the operation as failed", ex.Detail);
    }

    [Fact]
    public async Task A_timed_out_child_is_reported_as_a_timeout()
    {
        var runner = new RecordingProcessRunner()
            .Respond(new ProcessResult(-1, string.Empty, string.Empty, TimedOut: true));
        var (driver, _) = Driver(runner);

        var ex = await Assert.ThrowsAsync<HypervisorOperationException>(
            () => driver.StopAsync("work-vm", CancellationToken.None));

        Assert.Contains("timed out", ex.Detail);
    }

    [Fact]
    public async Task Progress_forwards_the_drivers_own_output_but_never_the_result_line()
    {
        var runner = new RecordingProcessRunner().RespondStdout(
            string.Empty,
            "==> Creating VM 'work-vm'",
            "    VM created",
            "==> Configuring VM settings",
            """{"ok":true,"value":null}""");
        var (driver, _) = Driver(runner);
        var progress = new ProgressSink();

        await driver.CreateVmAsync(Descriptor, progress, CancellationToken.None);

        Assert.Equal(
            ["==> Creating VM 'work-vm'", "    VM created", "==> Configuring VM settings"],
            progress.Lines);
        Assert.DoesNotContain(progress.Lines, line => line.Contains("\"ok\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("work-vm'; Remove-VM -Name *; #")]
    [InlineData("work\nvm")]
    [InlineData("Work-VM")]
    [InlineData("")]
    public async Task A_vm_name_that_is_not_a_dns_label_never_reaches_powershell(string name)
    {
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")));

        await Assert.ThrowsAsync<InvalidPlatformArgumentException>(
            () => driver.RemoveVmAsync(name, null, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_quote_in_a_configured_value_is_escaped_rather_than_ending_the_string()
    {
        // The one value in the create script that is free text. Single-quoted PowerShell expands
        // nothing, so doubling the quote is the complete escape.
        var options = PlatformOptions.Create(o => o.SwitchName = "Sneaky' ; Remove-VM -Name '*");
        var (driver, runner) = Driver(new RecordingProcessRunner().Respond(Ok("null")), options);

        await driver.CreateVmAsync(Descriptor, null, CancellationToken.None);

        Assert.Contains(
            "$descriptor['SwitchName'] = 'Sneaky'' ; Remove-VM -Name ''*'",
            Script(runner[0]),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_character_in_a_configured_value_is_refused()
    {
        var options = PlatformOptions.Create(o => o.SwitchName = "Default\r\nRemove-VM -Name *");
        var (driver, runner) = Driver(new RecordingProcessRunner(), options);

        await Assert.ThrowsAsync<InvalidPlatformArgumentException>(
            () => driver.CreateVmAsync(Descriptor, null, CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    private static (HyperVDriver Driver, RecordingProcessRunner Runner) Driver(
        RecordingProcessRunner runner,
        ConstructdOptions? options = null,
        LogSink? logs = null) =>
        (new HyperVDriver(
                runner,
                options ?? PlatformOptions.Create(),
                logs is null ? NullLogger<HyperVDriver>.Instance : logs.Logger<HyperVDriver>()),
            runner);

    private static ProcessResult Ok(string valueJson) =>
        new(0, $$"""{"ok":true,"value":{{valueJson}}}""", string.Empty, TimedOut: false);

    /// <summary>The script the service would actually have run.</summary>
    private static string Script(RecordedProcess call) => PowerShellEncoding.Decode(call.Arguments[^1]);
}
