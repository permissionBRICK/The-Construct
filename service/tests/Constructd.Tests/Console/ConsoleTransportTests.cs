using System.Text;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Console;

namespace Constructd.Tests.Console;

public sealed class ConsoleTransportTests
{
    private const string Screen = """{"nativeWidth":1024,"nativeHeight":768,"videoHeadPresent":true,"keyboardPresent":true,"syntheticMousePresent":true,"ps2MousePresent":false}""";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    [Fact]
    public void The_entire_Wmi_program_is_pinned_independently_of_the_argv_builder() =>
        Assert.Equal("a9e38b4ccf22e7ea1bdabe2d4cac52a03fc7c5521348d1c8aa96a99ff27b877e", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(HyperVConsoleScript.Source))));

    private static void Invocation(RecordedProcess call, string action)
    {
        Assert.Equal("powershell.exe", call.FileName);
        Assert.Equal(new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(HyperVConsoleScript.Source)) }, call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(20), call.Timeout);
        using var doc = JsonDocument.Parse(call.StandardInput!);
        Assert.Equal(action, doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("probe-vm", doc.RootElement.GetProperty("vm").GetString());
    }
    [Fact]
    public async Task Screen_invocation_and_all_fields()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Screen);
        var actual = await new HyperVConsoleTransport(runner).GetScreenAsync("probe-vm", default);
        Assert.Equal(new(1024, 768, true, true, true, false), actual); Invocation(runner[0], "screen");
    }
    [Fact]
    public async Task ConfiguredPowerShellExecutableIsUsed()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Screen);
        var options = new Constructd.Core.Configuration.ConstructdOptions { PowerShellPath = @"D:\Windows\powershell.exe" };
        await new HyperVConsoleTransport(runner, options).GetScreenAsync("probe-vm", default);
        Assert.Equal(options.PowerShellPath, runner[0].FileName);
    }
    [Theory]
    [InlineData("text", null)] [InlineData("key", true)] [InlineData("key", false)] [InlineData("key", null)]
    [InlineData("scancodes", null)] [InlineData("ctrlAltDel", null)]
    public async Task Every_keyboard_invocation_keeps_payload_in_stdin(string kind, bool? press)
    {
        var runner = new RecordingProcessRunner().RespondStdout("""{"applied":true,"returnValue":0,"device":"keyboard","fallback":null}""");
        var input = new KeyboardInput(Enum.Parse<KeyboardInputKind>(kind, true), kind == "text" ? "Grüße" : null,
            kind == "key" ? 13 : null, press, kind == "scancodes" ? new byte[] { 15, 143 } : null);
        Assert.True((await new HyperVConsoleTransport(runner).KeyboardAsync("probe-vm", input, default)).Applied);
        Invocation(runner[0], "keyboard");
        var expected = kind switch
        {
            "text" => """{"action":"keyboard","vm":"probe-vm","input":{"kind":"text","text":"Gr\u00FC\u00DFe","keyCode":null,"press":null,"scancodes":null}}""",
            "key" when press == true => """{"action":"keyboard","vm":"probe-vm","input":{"kind":"key","text":null,"keyCode":13,"press":true,"scancodes":null}}""",
            "key" when press == false => """{"action":"keyboard","vm":"probe-vm","input":{"kind":"key","text":null,"keyCode":13,"press":false,"scancodes":null}}""",
            "key" => """{"action":"keyboard","vm":"probe-vm","input":{"kind":"key","text":null,"keyCode":13,"press":null,"scancodes":null}}""",
            "scancodes" => """{"action":"keyboard","vm":"probe-vm","input":{"kind":"scancodes","text":null,"keyCode":null,"press":null,"scancodes":[15,143]}}""",
            _ => """{"action":"keyboard","vm":"probe-vm","input":{"kind":"ctrlAltDel","text":null,"keyCode":null,"press":null,"scancodes":null}}"""
        };
        Assert.Equal(expected, runner[0].StandardInput);
        Assert.All(runner[0].StandardInput!, c => Assert.True(c <= 127, "stdin must remain ASCII regardless of the Windows input code page"));
        using var doc = JsonDocument.Parse(runner[0].StandardInput!);
        Assert.Equal(kind, doc.RootElement.GetProperty("input").GetProperty("kind").GetString());
        if (input.Text is not null) Assert.Equal(input.Text, doc.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.DoesNotContain("secret-", string.Join(" ", runner[0].Arguments));
    }
    [Theory]
    [InlineData(MouseInputKind.MoveAbsolute)] [InlineData(MouseInputKind.MoveRelative)] [InlineData(MouseInputKind.Click)]
    [InlineData(MouseInputKind.Press)] [InlineData(MouseInputKind.Release)]
    public async Task Every_mouse_invocation_and_failure_fields(MouseInputKind kind)
    {
        var runner = new RecordingProcessRunner().RespondStdout("""{"applied":false,"returnValue":32768,"device":"syntheticMouse","fallback":"moveRelative"}""");
        var i = kind == MouseInputKind.MoveAbsolute ? new(kind, 1, 2, null, null, null) : kind == MouseInputKind.MoveRelative ?
            new(kind, null, null, -128, 127, null) : new MouseInput(kind, null, null, null, null, 1);
        Assert.Equal(new(false, 32768, "syntheticMouse", "moveRelative"), await new HyperVConsoleTransport(runner).MouseAsync("probe-vm", i, default));
        Invocation(runner[0], "mouse");
        var expected = kind switch
        {
            MouseInputKind.MoveAbsolute => """{"action":"mouse","vm":"probe-vm","input":{"kind":"moveAbsolute","x":1,"y":2,"dx":null,"dy":null,"button":null}}""",
            MouseInputKind.MoveRelative => """{"action":"mouse","vm":"probe-vm","input":{"kind":"moveRelative","x":null,"y":null,"dx":-128,"dy":127,"button":null}}""",
            MouseInputKind.Click => """{"action":"mouse","vm":"probe-vm","input":{"kind":"click","x":null,"y":null,"dx":null,"dy":null,"button":1}}""",
            MouseInputKind.Press => """{"action":"mouse","vm":"probe-vm","input":{"kind":"press","x":null,"y":null,"dx":null,"dy":null,"button":1}}""",
            _ => """{"action":"mouse","vm":"probe-vm","input":{"kind":"release","x":null,"y":null,"dx":null,"dy":null,"button":1}}"""
        };
        Assert.Equal(expected, runner[0].StandardInput);
    }
    [Fact]
    public async Task Screenshot_png_and_numeric_failure()
    {
        var runner = new RecordingProcessRunner().RespondStdout($$"""{"returnValue":0,"width":1,"height":1,"png":"{{Png}}"}""")
            .RespondStdout("""{"returnValue":32775}""");
        var driver = new HyperVConsoleTransport(runner);
        Assert.Equal(Convert.FromBase64String(Png), (await driver.ScreenshotAsync("probe-vm", 1, 1, default)).Png.ToArray());
        Invocation(runner[0], "screenshot");
        var failure = await driver.ScreenshotAsync("probe-vm", 1, 1, default);
        Assert.False(failure.Ok); Assert.Equal(32775u, failure.ReturnValue); Assert.True(failure.Png.IsEmpty);
    }
    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(0, true)]
    public async Task Process_and_parse_failures_are_sanitized(int code, bool timeout)
    {
        var runner = new RecordingProcessRunner().Respond(new ProcessResult(code, "SECRET", "SECRET", timeout));
        var e = await Assert.ThrowsAsync<ConsoleTransportException>(() => new HyperVConsoleTransport(runner).GetScreenAsync("probe-vm", default));
        Assert.DoesNotContain("SECRET", e.ToString()); Assert.Null(e.InnerException);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"applied\":true,\"returnValue\":32768,\"device\":\"keyboard\",\"fallback\":null}")]
    [InlineData("{\"applied\":true,\"returnValue\":0,\"device\":\"SECRET\",\"fallback\":null}")]
    public async Task Malformed_input_results_are_not_success(string response)
    {
        var runner = new RecordingProcessRunner().RespondStdout(response);
        await Assert.ThrowsAsync<ConsoleTransportException>(() => new HyperVConsoleTransport(runner).KeyboardAsync("probe-vm", new(KeyboardInputKind.CtrlAltDel, null, null, null, null), default));
    }
    [Theory]
    [InlineData("{}", false)] [InlineData("{\"tooLarge\":true}", true)]
    [InlineData("{\"returnValue\":0,\"width\":1,\"height\":1,\"png\":\"U0VDUkVU\"}", false)]
    public async Task Malformed_or_oversize_images_are_refused(string response, bool tooLarge)
    {
        var runner = new RecordingProcessRunner().RespondStdout(response);
        var e = await Assert.ThrowsAsync<ConsoleTransportException>(() => new HyperVConsoleTransport(runner).ScreenshotAsync("probe-vm", 1, 1, default));
        Assert.Equal(tooLarge, e.TooLarge);
    }
    [Fact]
    public async Task Invalid_requests_do_not_launch_a_process()
    {
        var runner = new RecordingProcessRunner(); var driver = new HyperVConsoleTransport(runner);
        await Assert.ThrowsAsync<ConsoleTransportException>(() => driver.ScreenshotAsync("probe-vm", int.MaxValue, int.MaxValue, default));
        await Assert.ThrowsAsync<ConsoleTransportException>(() => driver.KeyboardAsync("probe-vm", new(KeyboardInputKind.Text, new string('x', 513), null, null, null), default));
        await Assert.ThrowsAsync<ConsoleTransportException>(() => driver.MouseAsync("probe-vm", new(MouseInputKind.MoveRelative, null, null, 128, 0, null), default));
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public void Session_cap_expiry_rate_and_dimensions_are_atomic()
    {
        var store = new InMemoryConsoleSessionStore(); var now = DateTimeOffset.UtcNow;
        Parallel.For(0, 16, _ => store.TryCreate("probe-vm", "owner", 1, 1, ConsoleSessionRules.Ttl, now));
        Assert.Null(store.TryCreate("PROBE-VM", "owner", 1, 1, ConsoleSessionRules.Ttl, now));
        Assert.Equal(4, store.RemoveExpired(now.AddSeconds(60)));
        var s = store.TryCreate("probe-vm", "owner", 1, 1, ConsoleSessionRules.Ttl, now)!;
        var accepted = 0; Parallel.For(0, 100, _ => { if (store.TryTakeRate(s.Id, "input", 50, now)) Interlocked.Increment(ref accepted); });
        Assert.Equal(50, accepted);
        Assert.Equal(20, store.Renew(s.Id, ConsoleSessionRules.Ttl, now, 20, 10)!.NativeWidth);
        Assert.True(store.TryTakeRate(s.Id, "input", 50, now.AddSeconds(1)));
        Assert.Equal(1, store.RemoveForPrincipal("OWNER")); Assert.Null(store.Get(s.Id, now));
    }
}
