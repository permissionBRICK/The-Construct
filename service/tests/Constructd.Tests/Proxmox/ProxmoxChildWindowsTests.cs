using System.Text.Json;
using Constructd.Core.Logic;
namespace Constructd.Tests.Proxmox;
public sealed partial class ProxmoxChildDriverTests
{
    [Fact]
    public async Task Windows_key_uses_guest_stdin_and_checks_incarnation()
    {
        await Create(); var incarnation = JsonDocument.Parse(File.ReadAllText(Marker)).RootElement.GetProperty("Incarnation").GetString()!;
        NamedConfig();
        runner.Respond(call =>
        {
            Assert.Equal("ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", call.StandardInput);
            Assert.DoesNotContain(call.Arguments, a => a.Contains("ABCDE"));
            Assert.Contains("--pass-stdin", call.Arguments);
            return Ok(new Dictionary<string,object> { ["exitcode"] = 0, ["out-data"] = "{\"ok\":true}" });
        });
        await driver.DeliverWindowsKeyAsync("child", incarnation, "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", default);
        NamedConfig();
        await Assert.ThrowsAsync<ChildValidationException>(() => driver.DeliverWindowsKeyAsync("child", Guid.NewGuid().ToString(), "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY", default));
    }
    [Fact]
    public async Task Windows_ejection_changes_only_CD_drives_and_verifies_readback()
    {
        await Create(); var incarnation = JsonDocument.Parse(File.ReadAllText(Marker)).RootElement.GetProperty("Incarnation").GetString()!;
        NamedConfig(); QueryConfig();
        runner.Respond(call => { Assert.Equal(new[] { "set", "101", "--ide2", "none,media=cdrom" }, call.Arguments); config["ide2"] = "none,media=cdrom"; return Ok(new { }); });
        QueryConfig(); await driver.EjectMediaAsync("child", incarnation, true, default);
        Assert.Contains("media=cdrom", config["ide0"].ToString());
    }
}
