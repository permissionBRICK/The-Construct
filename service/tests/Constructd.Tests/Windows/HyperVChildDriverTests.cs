using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.HyperV;
namespace Constructd.Tests.Windows;

public sealed class HyperVChildDriverTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly ChildHardware Hardware = new(2, 512, 1, 2, true, SecureBootTemplate.MicrosoftWindows, true, [BootDevice.InstallMedia, BootDevice.AuxiliaryMedia, BootDevice.Disk, BootDevice.Network], true);
    private static (HyperVChildDriver Driver, Constructd.Fakes.RecordingProcessRunner Runner) Create()
    {
        var hypervisor = new FakeHypervisorDriver();
        var caps = new FakeChildVmDriver(hypervisor).Capabilities;
        var runner = new Constructd.Fakes.RecordingProcessRunner { Default = Ok(null) };
        runner.Respond(Ok(caps));
        return (new(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct", VmStorageRoot = @"C:\VMs" }, hypervisor), runner);
    }
    private static ProcessResult Ok(object? value) => new(0, JsonSerializer.Serialize(new { ok = true, value }, Json), "", false);
    private static string Script(RecordedProcess call) => Encoding.Unicode.GetString(Convert.FromBase64String(call.Arguments[5]));

    [Fact]
    public async Task CreateUsesFixedArgvAndStdinDescriptorWithoutPrimaryProvisioning()
    {
        var (driver, runner) = Create();
        await driver.CreateOwnedAsync(new("child", Hardware, null, @"C:\media\install.iso", @"C:\media\answer.iso", "Default Switch"), "job-operation", null, default);
        var call = runner.Calls[1];
        Assert.Equal("powershell.exe", call.FileName);
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand" }, call.Arguments.Take(5));
        Assert.Equal(6, call.Arguments.Count);
        Assert.Contains("-Include ChildVm", Script(call));
        Assert.Contains("New-ConstructChildVm -Descriptor $inputData", Script(call));
        Assert.DoesNotContain("answer.iso", string.Join(" ", call.Arguments));
        Assert.DoesNotContain("Wait-ConstructVmReachable", Script(call));
        var input = JsonDocument.Parse(call.StandardInput!).RootElement;
        Assert.Equal(@"C:\VMs\child.vhdx", input.GetProperty("vhdPath").GetString());
        Assert.Equal("job-operation", input.GetProperty("operationId").GetString());
        Assert.Equal(@"C:\VMs\child.vhdx.childvm.json", input.GetProperty("ownershipPath").GetString());
        Assert.Equal("microsoftWindows", input.GetProperty("hardware").GetProperty("secureBootTemplate").GetString());
    }
    [Fact]
    public async Task ExplicitDiskPathKeepsItsCleanupMarkerAtCanonicalLocation()
    {
        var (driver, runner) = Create();
        await driver.CreateOwnedAsync(new("child", Hardware, @"D:\custom\disk.vhdx", @"C:\media\install.iso", @"C:\media\aux.iso", "Default Switch"), "operation", null, default);
        var data = JsonDocument.Parse(runner.Calls[1].StandardInput!).RootElement;
        Assert.Equal(@"D:\custom\disk.vhdx", data.GetProperty("vhdPath").GetString());
        Assert.Equal(@"C:\VMs\child.vhdx.childvm.json", data.GetProperty("ownershipPath").GetString());
        await driver.RemoveAsync("child", null, default);
        Assert.Equal(@"C:\VMs\child.vhdx", JsonDocument.Parse(runner.Calls[2].StandardInput!).RootElement.GetProperty("vhdPath").GetString());
    }
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task UnsupportedRequestsNeverAllocate(int generation, bool dynamic)
    {
        var (driver, runner) = Create();
        var hardware = Hardware with { Generation = generation, DynamicMemory = dynamic ? new(512, 512, 512) : null };
        var error = await Assert.ThrowsAsync<ChildValidationException>(() => driver.CreateAsync(new("child", hardware, null, null, null, "Default Switch"), null, default));
        Assert.Equal("unsupported-capability", error.Code);
        Assert.Single(runner.Calls);
    }
    [Fact]
    public async Task HardwareAndMediaCommandsPinShape()
    {
        var (driver, runner) = Create();
        await driver.UpdateHardwareAsync("child", Hardware, false, default);
        await driver.SetMediaAsync("child", null, null, [BootDevice.Disk], default);
        Assert.Contains("-ResendTemplate $inputData.resendTemplate", Script(runner.Calls[1]));
        Assert.False(JsonDocument.Parse(runner.Calls[1].StandardInput!).RootElement.GetProperty("resendTemplate").GetBoolean());
        Assert.Contains("Set-ConstructChildMedia -Name $inputData.name -InstallMediaPath $inputData.installMediaPath -AuxiliaryMediaPath $inputData.auxiliaryMediaPath -BootOrder $inputData.bootOrder", Script(runner.Calls[2]));
    }
    [Theory]
    [InlineData("completed", GracefulShutdownOutcome.Completed)]
    [InlineData("unavailable", GracefulShutdownOutcome.Unavailable)]
    [InlineData("timeout", GracefulShutdownOutcome.Timeout)]
    [InlineData("failed", GracefulShutdownOutcome.Failed)]
    public async Task GracefulShutdownHasDistinctOutcomes(string wire, GracefulShutdownOutcome expected)
    {
        var (driver, runner) = Create();
        await driver.GetCapabilitiesAsync(default);
        runner.Respond(Ok(wire));
        Assert.Equal(expected, await driver.ShutdownGracefulAsync("child", TimeSpan.FromSeconds(3), null, default));
        var call = runner.Calls[1];
        Assert.Contains("Stop-ConstructChildVmGracefully -Name $inputData.name -TimeoutSeconds $inputData.timeoutSeconds", Script(call));
        Assert.Equal(TimeSpan.FromSeconds(63), call.Timeout);
        Assert.DoesNotContain("-TurnOff", Script(call)); Assert.DoesNotContain("-Force", Script(call));
    }
    [Fact]
    public async Task QueriesAndRemovalPinFunctionAndNamePayload()
    {
        var (driver, runner) = Create(); await driver.GetCapabilitiesAsync(default);
        runner.Respond(Ok("guid")); Assert.Equal("guid", await driver.GetVmIdAsync("child", default));
        runner.Respond(Ok(new AttachedMedia(null, null, true))); Assert.True((await driver.GetAttachedMediaAsync("child", default)).Complete);
        runner.Respond(Ok(new ChildStoragePlacement(@"C:\VMs\child.vhdx", @"C:\", @"C:\"))); await driver.ResolveStorageAsync("child", default);
        await driver.RemoveAsync("child", null, default);
        runner.Respond(Ok(new VmCapabilitiesSnapshot("child", VmState.Off, true, true, true, false, 1024, 768, true, 2, CapabilityLevel.Conditional, new(CapabilityLevel.Supported, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported))));
        Assert.True((await driver.GetVmCapabilitiesAsync("child", default)).SecureBootTemplateLocked);
        runner.Respond(Ok("operation")); Assert.Equal("operation", await driver.GetCreationOperationAsync("child", default));
        var functions = new[] { "Get-ConstructChildVmId", "Get-ConstructChildAttachedMedia", "Get-ConstructChildStorage", "Remove-ConstructChildVm", "Get-ConstructChildVmCapabilities", "Get-ConstructChildCreationOperation" };
        for (var i = 0; i < functions.Length; i++)
        { Assert.Contains(functions[i] + " -Name $inputData.name", Script(runner.Calls[i + 1])); Assert.Equal("child", JsonDocument.Parse(runner.Calls[i + 1].StandardInput!).RootElement.GetProperty("name").GetString()); }
    }
    [Fact]
    public async Task CapabilityFailureIsNotCachedAndDependencySecretsAreSuppressed()
    {
        var (driver, runner) = Create();
        runner.Failure = new Exception("secret-from-dependency");
        var error = await Assert.ThrowsAsync<HypervisorOperationException>(() => driver.GetCapabilitiesAsync(default));
        Assert.DoesNotContain("secret", error.ToString());
        runner.Failure = null;
        await driver.GetCapabilitiesAsync(default); await driver.GetCapabilitiesAsync(default);
        Assert.Equal(2, runner.Calls.Count);
    }
    [Theory]
    [InlineData("../other")]
    [InlineData("child; Stop-VM")]
    public async Task InvalidNamesNeverLaunch(string name)
    {
        var (driver, runner) = Create();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => driver.RemoveAsync(name, null, default));
        Assert.Empty(runner.Calls);
    }
}
