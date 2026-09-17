using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Proxmox;

namespace Constructd.Tests.Proxmox;

public sealed partial class ProxmoxChildDriverTests
{
    private void OffConfig(string state = "stopped")
    {
        runner.RespondStdout(Resources).RespondStdout("{\"status\":\"" + state + "\"}");
        if (state == "stopped") QueryConfig();
    }
    private void ApplySet()
    {
        runner.Respond(call =>
        {
            Assert.Equal("set", call.Arguments[0]);
            for (var i = 2; i < call.Arguments.Count; i += 2)
            {
                var key = call.Arguments[i][2..]; var value = call.Arguments[i + 1];
                if (key == "delete") { foreach (var slot in value.Split(',')) config.Remove(slot); }
                else if (key == "efidisk0") config[key] = value.Replace("local-lvm:1,", "local-lvm:vm-101-disk-3,");
                else if (key == "tpmstate0") config[key] = value.Replace("local-lvm:1,", "local-lvm:vm-101-disk-4,");
                else config[key] = value;
            }
            return new(0, "", "", false);
        });
    }
    [Fact]
    public async Task Cpu_ram_and_boot_order_update_preserves_template_without_resend()
    {
        await Create(); OffConfig(); ApplySet(); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.UpdateHardwareAsync("child", Hardware with { Cpus = 4, RamMb = 2048, SecureBootTemplate = SecureBootTemplate.MicrosoftUefiCertificateAuthority,
            BootOrder = [BootDevice.Disk, BootDevice.InstallMedia] }, false, default);
        var set = runner.Calls.Single(c => c.Arguments[0] == "set");
        Assert.Equal(new[] { "set", "101", "--cores", "4", "--sockets", "1", "--memory", "2048", "--balloon", "0", "--boot", "order=sata0;ide2" }, set.Arguments);
        Assert.Contains("template=microsoftWindows", config["description"].ToString());
    }
    [Fact]
    public async Task Template_can_change_after_tpm_init_and_both_templates_use_pre_enrolled_keys()
    {
        await Create(); OffConfig(); ApplySet(); ApplySet(); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.UpdateHardwareAsync("child", Hardware with { SecureBootTemplate = SecureBootTemplate.MicrosoftUefiCertificateAuthority }, true, default);
        var writes = runner.Calls.Where(c => c.Arguments[0] == "set").ToArray();
        Assert.Equal(new[] { "set", "101", "--delete", "efidisk0", "--force", "1" }, writes[0].Arguments);
        Assert.Contains("local-lvm:1,efitype=4m,pre-enrolled-keys=1", writes[1].Arguments);
        Assert.Contains("template=microsoftUefiCertificateAuthority", config["description"].ToString());
        Assert.True(config.ContainsKey("tpmstate0"));
    }
    [Fact]
    public async Task Secure_boot_and_tpm_can_be_disabled_then_enabled()
    {
        await Create(); OffConfig(); ApplySet(); ApplySet(); ApplySet(); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.UpdateHardwareAsync("child", Hardware with { SecureBoot = false, Tpm = false }, false, default);
        Assert.False(config.ContainsKey("tpmstate0")); Assert.Contains("pre-enrolled-keys=0", config["efidisk0"].ToString());
        OffConfig(); ApplySet(); ApplySet(); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.UpdateHardwareAsync("child", Hardware, false, default);
        Assert.Contains("version=v2.0", config["tpmstate0"].ToString()); Assert.Contains("pre-enrolled-keys=1", config["efidisk0"].ToString());
    }
    [Fact]
    public async Task Disk_grows_but_never_shrinks()
    {
        await Create(); OffConfig();
        Assert.Equal("validation", (await Assert.ThrowsAsync<ChildValidationException>(() => driver.UpdateHardwareAsync("child", Hardware with { DiskGb = 3 }, false, default))).Code);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] == "set");
        OffConfig(); ApplySet(); runner.Respond(call =>
        {
            Assert.Equal(new[] { "disk", "resize", "101", "sata0", "8G" }, call.Arguments);
            config["sata0"] = "local-lvm:vm-101-disk-1,size=8G"; return new(0, "", "", false);
        }); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.UpdateHardwareAsync("child", Hardware with { DiskGb = 8 }, false, default);
    }
    [Fact]
    public async Task Readback_mismatch_is_not_success()
    {
        await Create(); OffConfig(); runner.RespondStdout(""); QueryConfig();
        await Assert.ThrowsAsync<ProxmoxOperationException>(() => driver.UpdateHardwareAsync("child", Hardware with { Cpus = 8 }, false, default));
    }
    [Theory]
    [InlineData("running")] [InlineData("unknown")]
    public async Task Hardware_and_media_require_off(string state)
    {
        await Create(); OffConfig(state);
        Assert.Equal("vm-not-off", (await Assert.ThrowsAsync<ChildValidationException>(() => driver.UpdateHardwareAsync("child", Hardware, false, default))).Code);
        OffConfig(state);
        Assert.Equal("vm-not-off", (await Assert.ThrowsAsync<ChildValidationException>(() => driver.SetMediaAsync("child", null, null, [BootDevice.Disk], default))).Code);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] == "set");
    }
    [Fact]
    public async Task Media_detaches_both_slots_and_readback_handles_unknown_volumes()
    {
        await Create(); NamedConfig();
        Assert.Equal(new(Install, Auxiliary, true), await driver.GetAttachedMediaAsync("child", default));
        OffConfig(); ApplySet(); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.SetMediaAsync("child", null, null, [BootDevice.Disk], default);
        Assert.Equal(new[] { "set", "101", "--delete", "ide2,ide0", "--boot", "order=sata0" },
            runner.Calls.Single(c => c.Arguments[0] == "set").Arguments);
        NamedConfig(); Assert.Equal(new(null, null, true), await driver.GetAttachedMediaAsync("child", default));
        config["ide2"] = "foreign:iso/not-managed.iso,media=cdrom"; NamedConfig();
        Assert.False((await driver.GetAttachedMediaAsync("child", default)).Complete);
    }
    [Fact]
    public async Task Media_attaches_slots_and_filters_boot_devices_to_present_hardware()
    {
        await Create(); config.Remove("net0"); OffConfig(); ApplySet(); QueryConfig(); runner.RespondStdout("{\"status\":\"stopped\"}");
        await driver.SetMediaAsync("child", Auxiliary, null, [BootDevice.Network, BootDevice.AuxiliaryMedia, BootDevice.InstallMedia, BootDevice.Disk], default);
        var args = runner.Calls.Single(c => c.Arguments[0] == "set").Arguments;
        Assert.Contains("construct-media:iso/" + Path.GetFileName(Auxiliary) + ",media=cdrom", args);
        Assert.Contains("order=ide2;sata0", args);
        NamedConfig(); Assert.Equal(new(Auxiliary, null, true), await driver.GetAttachedMediaAsync("child", default));
    }
    [Theory]
    [InlineData(0, false, "", "stopped", GracefulShutdownOutcome.Completed)]
    [InlineData(0, false, "", "running", GracefulShutdownOutcome.Timeout)]
    [InlineData(1, false, "shutdown failed - got timeout", "running", GracefulShutdownOutcome.Timeout)]
    [InlineData(1, true, "", "running", GracefulShutdownOutcome.Timeout)]
    [InlineData(1, false, "QEMU guest agent is not running", "running", GracefulShutdownOutcome.Unavailable)]
    [InlineData(1, false, "secret failure", "running", GracefulShutdownOutcome.Failed)]
    public async Task Shutdown_outcomes_are_distinct_and_never_force_power_off(int exit, bool timedOut, string error, string state, GracefulShutdownOutcome expected)
    {
        await Create(); runner.RespondStdout(Resources).RespondStdout("{\"status\":\"running\"}"); QueryConfig();
        runner.Respond(new ProcessResult(exit, "", error, timedOut));
        if (exit == 0 && !timedOut) runner.RespondStdout("{\"status\":\"" + state + "\"}");
        Assert.Equal(expected, await driver.ShutdownGracefulAsync("child", TimeSpan.FromSeconds(3), null, default));
        var call = runner.Calls.Single(c => c.Arguments[0] == "shutdown");
        Assert.Equal(new[] { "shutdown", "101", "--timeout", "3" }, call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(63), call.Timeout);
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] is "stop" or "suspend" or "destroy");
    }
    [Fact]
    public async Task Shutdown_already_off_and_no_acpi_or_agent_do_not_launch_shutdown()
    {
        runner.RespondStdout(Resources).RespondStdout("{\"status\":\"stopped\"}");
        Assert.Equal(GracefulShutdownOutcome.Completed, await driver.ShutdownGracefulAsync("child", TimeSpan.FromSeconds(1), null, default));
        config["acpi"] = 0; config["agent"] = "enabled=0";
        runner.RespondStdout(Resources).RespondStdout("{\"status\":\"running\"}"); QueryConfig();
        Assert.Equal(GracefulShutdownOutcome.Unavailable, await driver.ShutdownGracefulAsync("child", TimeSpan.FromSeconds(1), null, default));
        Assert.DoesNotContain(runner.Calls, c => c.Arguments[0] == "shutdown");
    }
    [Fact]
    public async Task Vm_capabilities_reflect_config_and_runtime_state()
    {
        await Create(); config["vga"] = "none"; config["tablet"] = 0; config["agent"] = 0; config["acpi"] = 0;
        NamedConfig(); runner.RespondStdout("{\"status\":\"stopped\",\"lock\":\"suspended\"}");
        var caps = await driver.GetVmCapabilitiesAsync("child", default);
        Assert.Equal(VmState.Saved, caps.State); Assert.Equal(2, caps.Generation); Assert.False(caps.SecureBootTemplateLocked);
        Assert.False(caps.VideoHeadPresent); Assert.False(caps.SyntheticMousePresent); Assert.True(caps.Ps2MousePresent);
        Assert.Equal(CapabilityLevel.Unsupported, caps.GracefulShutdown); Assert.Equal(CapabilityLevel.Unsupported, caps.Network.HostForward);
    }
}
