using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Windows.HyperV;
using static Constructd.Tests.Capacity.CapacityMathTests;
namespace Constructd.Tests.Capacity;

public class HyperVInventoryTests
{
    [Fact]
    public async Task PinsArgvAndArtifactStdinAndMapsOneEpoch()
    {
        var runner = new RecordingProcessRunner(); var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        runner.RespondStdout(JsonSerializer.Serialize(new { ok = true, value = Inventory() }, json));
        var clock = new MutableClock(Now); var inventory = new HyperVInventory(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, clock);
        var snapshot = await inventory.ReadAsync([Row(Constructd.Core.Domain.ReservationResource.Storage, artifact: @"disk:C:\VMs\a.vhdx")], default);
        Assert.True(snapshot.Complete); Assert.Equal(1, snapshot.Epoch);
        var call = Assert.Single(runner.Calls); Assert.Equal("powershell.exe", call.FileName);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" }, call.Arguments.Take(5));
        Assert.Equal(6, call.Arguments.Count); Assert.Equal(TimeSpan.FromMinutes(2), call.Timeout);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[5]));
        Assert.Contains(". 'C:\\Construct\\drivers\\hyperv-local\\HyperVLocal.ChildVm.ps1'", script);
        Assert.Contains("Get-ConstructHostInventory -Artifacts @($inputData.artifacts)", script);
        Assert.Contains("[Console]::In.ReadToEnd()", script); Assert.DoesNotContain("a.vhdx", script);
        using var stdin = JsonDocument.Parse(call.StandardInput!);
        Assert.Equal(@"C:\VMs\a.vhdx", stdin.RootElement.GetProperty("artifacts")[0].GetProperty("path").GetString());
    }
    [Theory]
    [InlineData("not json")] [InlineData("{\"ok\":false,\"error\":\"sentinel-secret\"}")]
    [InlineData("{\"ok\":true,\"value\":{}}")]
    public async Task MalformedAndFailedOutputFailsClosedWithoutLeaking(string output)
    {
        var runner = new RecordingProcessRunner().RespondStdout(output);
        var inventory = new HyperVInventory(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, new MutableClock());
        var snapshot = await inventory.ReadAsync(default); Assert.False(snapshot.Complete);
        Assert.Equal(["inventory-unavailable"], snapshot.Problems);
    }
    [Fact]
    public async Task RunnerFailureAndTimeoutDoNotLeakDependencyText()
    {
        var runner = new RecordingProcessRunner { Failure = new IOException("sentinel-secret") };
        var inventory = new HyperVInventory(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, new MutableClock());
        Assert.Equal(["inventory-unavailable"], (await inventory.ReadAsync(default)).Problems);
        runner.Failure = null; runner.Default = new(1, "sentinel-secret", "sentinel-secret", true);
        Assert.False((await inventory.ReadAsync(default)).Complete);
    }
}
