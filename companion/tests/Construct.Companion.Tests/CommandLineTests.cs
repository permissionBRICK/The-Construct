using System.Text.Json;
using Construct.Companion.Core;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;

namespace Construct.Companion.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void EmptyCommandHasNoRequestedView() => Assert.Equal(new CommandLine(), CommandLine.Parse([]));

    [Theory]
    [InlineData("--background")]
    [InlineData("--panel")]
    [InlineData("--settings")]
    [InlineData("--hostadmin")]
    [InlineData("--popup")]
    [InlineData("--quit")]
    [InlineData("--selftest")]
    [InlineData("--json")]
    [InlineData("--version")]
    public void BooleanOptionsAreParsed(string flag)
    {
        var command = CommandLine.Parse([flag]);
        var json = JsonSerializer.SerializeToElement(command, IpcJson.Options);
        var key = flag[2..] switch { "hostadmin" => "hostAdmin", "selftest" => "selfTest", var name => name };
        Assert.True(json.GetProperty(key).GetBoolean());
    }

    [Fact]
    public void OptionsCombineAndValuesStayOpaque()
    {
        var command = CommandLine.Parse(["--background", "--panel", "--settings", "--instance", "dev", "--hostadmin", "--host", "lab", "--popup", "--uri", "construct://forward?instance=dev&id=a", "--selftest", "--json"]);
        Assert.Equal(new CommandLine(true, true, true, true, true, "dev", "lab", "construct://forward?instance=dev&id=a", SelfTest: true, Json: true), command);
    }

    [Theory]
    [InlineData("--instance")]
    [InlineData("--host")]
    [InlineData("--uri")]
    public void ValuesAreRequired(string option)
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse([option]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse([option, "--popup"]));
        Assert.Throws<ArgumentException>(() => CommandLine.Parse([option, " "]));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("powershell:bad")]
    [InlineData("relative")]
    public void UriMustUseConstructScheme(string value) => Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--uri", value]));

    [Fact]
    public void UnknownOptionErrorDoesNotEchoInput()
    {
        var error = Assert.Throws<ArgumentException>(() => CommandLine.Parse(["private-input"]));
        Assert.DoesNotContain("private-input", error.Message);
    }

    [Fact]
    public void SelfTestExitOnlyDependsOnLocalRequiredChecks()
    {
        var report = SelfTestReport.Create([new("paths", "passed"),new("ssh:dev","unreachable",false)]);
        Assert.True(report.Ok); Assert.Equal(0,report.ExitCode);
        Assert.Equal(1,SelfTestReport.Create([new("webView2","failed")]).ExitCode);
    }

    [Fact]
    public void WireNullsAndDefaultForwardSettingsMatchTheContract()
    {
        var host = JsonSerializer.SerializeToElement(new RemoteHost("lab", "https://host", "token", true, null), IpcJson.Options);
        Assert.Equal(JsonValueKind.Null, host.GetProperty("admin").ValueKind);
        var manifest = JsonSerializer.SerializeToElement(new InstallManifest(1, "commit", "version", "local-build", null, DateTimeOffset.UnixEpoch), IpcJson.Options);
        Assert.Equal(JsonValueKind.Null, manifest.GetProperty("releaseTag").ValueKind);
        var settings = JsonSerializer.SerializeToElement(new CompanionSettings(), IpcJson.Options);
        Assert.True(settings.GetProperty("forwards").GetProperty("enabled").GetBoolean());
        var activation = JsonSerializer.SerializeToElement(new UiActivation("panel"), IpcJson.Options);
        Assert.False(activation.TryGetProperty("host", out _));
    }

    [Fact]
    public void MessagesPassThroughWithoutLosingUnknownFields()
    {
        using var raw = JsonDocument.Parse("{\"type\":\"future\",\"nested\":{\"array\":[null,1,true,\"x\"]}}");
        var envelope = new InstanceMessage("dev", raw.RootElement);
        var wire = JsonSerializer.Serialize(envelope, IpcJson.Options);
        var copy = JsonSerializer.Deserialize<InstanceMessage>(wire, IpcJson.Options)!;
        Assert.Equal("dev", copy.Instance);
        Assert.Equal(raw.RootElement.GetRawText(), copy.Message.GetRawText());
        Assert.Contains("\"instance\"", wire);
        Assert.DoesNotContain("\"Instance\"", wire);
    }
}
