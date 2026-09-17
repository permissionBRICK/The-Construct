using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Proxmox;
using Constructd.Tests.Support;
using Constructd.Windows.Process;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Proxmox;

public sealed class ProxmoxConsoleTests
{
    private const string Resources = """[{"type":"qemu","node":"pve1","name":"child","vmid":101}]""";
    private static readonly byte[] Ppm = [.. Encoding.ASCII.GetBytes("P6\n# QEMU fixture\n2 1\n255\n"), 10, 35, 32, 255, 0, 1];
    private static ConstructdOptions Options()
    {
        var options = new ConstructdOptions { PublicHost = "pve.example.test" };
        options.Proxmox.Node = "pve1"; return options;
    }
    private static JsonElement Request(RecordedProcess call) => JsonDocument.Parse(call.StandardInput!).RootElement.Clone();
    private static ProcessResult Ack(RecordedProcess call) => new(0,
        JsonSerializer.Serialize(Enumerable.Range(0, Request(call).GetProperty("commands").GetArrayLength()).Select(_ => new { })), "", false);
    private static JsonElement Events(RecordedProcess call, int index = 0) => Request(call).GetProperty("commands")[index].GetProperty("arguments").GetProperty("events");
    private static string? Key(JsonElement e) => e.GetProperty("data").GetProperty("key").GetProperty("data").GetString();
    private static void Capture(RecordingProcessRunner runner, Action<string>? remember = null) => runner.Respond(call =>
    {
        var path = Request(call).GetProperty("commands")[0].GetProperty("arguments").GetProperty("filename").GetString()!;
        remember?.Invoke(path); File.WriteAllBytes(path, Ppm); return Ack(call);
    });

    [Fact]
    public void Ppm_fixture_encodes_native_RGB_and_valid_PNG_chunks_including_pixel_whitespace()
    {
        var pixels = PpmPngEncoder.Read(Ppm); Assert.Equal(2, pixels.Width); Assert.Equal(1, pixels.Height);
        var png = PpmPngEncoder.Encode(pixels);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var types = new List<string>();
        for (var offset = 8; offset < png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4); types.Add(type);
            uint crc = uint.MaxValue;
            foreach (var b in png.AsSpan(offset + 4, length + 4))
            {
                crc ^= b;
                for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u * (crc & 1));
            }
            Assert.Equal(~crc, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length)));
            if (type == "IHDR")
            {
                Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 8)));
                Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset + 12)));
            }
            if (type == "IDAT")
            {
                using var source = new MemoryStream(png, offset + 8, length);
                using var zlib = new ZLibStream(source, CompressionMode.Decompress);
                using var decoded = new MemoryStream(); zlib.CopyTo(decoded);
                Assert.Equal(new byte[] { 0, 10, 35, 32, 255, 0, 1 }, decoded.ToArray());
            }
            offset += length + 12;
        }
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, types);
    }
    [Theory]
    [InlineData("P3\n1 1\n255\nabc")]
    [InlineData("P6\n-1 1\n255\nabc")]
    [InlineData("P6\n1 1\n65535\nabc")]
    [InlineData("P6\n1 1\n255\na")]
    [InlineData("P6\n1 1\n255\nabcd")]
    public void Malformed_PPM_is_rejected(string fixture) =>
        Assert.Throws<ConsoleTransportException>(() => PpmPngEncoder.Read(Encoding.ASCII.GetBytes(fixture)));

    [Fact]
    public void Pixel_and_compressed_size_caps_are_enforced()
    {
        Assert.True(Assert.Throws<ConsoleTransportException>(() => PpmPngEncoder.Read("P6\n65535 65535\n255\n"u8.ToArray())).TooLarge);
        var rgb = new byte[2048 * 1024 * 3]; new Random(17).NextBytes(rgb);
        Assert.True(Assert.Throws<ConsoleTransportException>(() => PpmPngEncoder.Encode(new(2048, 1024, rgb))).TooLarge);
    }
    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task Screenshot_uses_native_resolution_and_cleans_private_file(int width, bool success)
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources); string? path = null;
        Capture(runner, p => path = p); var transport = new ProxmoxConsoleTransport(runner, Options());
        if (success) Assert.True((await transport.ScreenshotAsync("child", width, 1, default)).Ok);
        else await Assert.ThrowsAsync<ConsoleTransportException>(() => transport.ScreenshotAsync("child", width, 1, default));
        Assert.NotNull(path); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal(new[] { "-c", ProxmoxQmp.Script, "/run/qemu-server/101.qmp" }, runner.Calls[1].Arguments);
        Assert.Equal("python3", runner.Calls[1].FileName);
    }
    [Fact]
    public async Task Failed_screendump_cleans_file_and_sanitizes_dependency_output()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources); string? path = null;
        runner.Respond(call => { path = Request(call).GetProperty("commands")[0].GetProperty("arguments").GetProperty("filename").GetString()!;
            File.WriteAllText(path, "partial"); return new(1, "secret", "secret", false); });
        var error = await Assert.ThrowsAsync<ConsoleTransportException>(() => new ProxmoxConsoleTransport(runner, Options()).ScreenshotAsync("child", 2, 1, default));
        Assert.DoesNotContain("secret", error.ToString()); Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Virtual_keys_preserve_press_release_semantics(bool? press)
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources).Respond(Ack);
        await new ProxmoxConsoleTransport(runner, Options()).KeyboardAsync("child", new(KeyboardInputKind.Key, null, 65, press, null), default);
        var events = Events(runner.Calls[1]); Assert.Equal(press is null ? 2 : 1, events.GetArrayLength());
        Assert.All(events.EnumerateArray(), e => Assert.Equal("a", Key(e)));
        Assert.Equal(press ?? true, events[0].GetProperty("data").GetProperty("down").GetBoolean());
        Assert.True(Request(runner.Calls[1]).GetProperty("running").GetBoolean());
    }
    [Fact]
    public async Task Text_stays_on_stdin_with_US_layout_shift_and_pacing()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources).Respond(Ack);
        await new ProxmoxConsoleTransport(runner, Options()).KeyboardAsync("child", new(KeyboardInputKind.Text, "Secret!\n", null, null, null), default);
        Assert.DoesNotContain("Secret!", string.Join(" ", runner.Calls[1].Arguments));
        Assert.Equal(new[] { "shift", "s", "s", "shift" }, Events(runner.Calls[1]).EnumerateArray().Select(Key));
        Assert.Equal("ret", Key(Events(runner.Calls[1], 7)[0]));
        Assert.True(Request(runner.Calls[1]).GetProperty("paced").GetBoolean());
    }
    [Fact]
    public async Task Scancodes_and_CtrlAltDel_include_release_events()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources).Respond(Ack).RespondStdout(Resources).Respond(Ack);
        var transport = new ProxmoxConsoleTransport(runner, Options());
        await transport.KeyboardAsync("child", new(KeyboardInputKind.Scancodes, null, null, null, [0x1e, 0x9e, 0xe0, 0x53, 0xe0, 0xd3]), default);
        Assert.Equal("delete", Key(Events(runner.Calls[1], 2)[0]));
        Assert.False(Events(runner.Calls[1], 3)[0].GetProperty("data").GetProperty("down").GetBoolean());
        await transport.KeyboardAsync("child", new(KeyboardInputKind.CtrlAltDel, null, null, null, null), default);
        Assert.Equal(new[] { "ctrl", "alt", "delete", "delete", "alt", "ctrl" }, Events(runner.Calls[3]).EnumerateArray().Select(Key));
    }
    [Fact]
    public async Task Unsupported_unicode_and_incomplete_scancodes_apply_nothing()
    {
        var runner = new RecordingProcessRunner(); var transport = new ProxmoxConsoleTransport(runner, Options());
        await Assert.ThrowsAsync<ConsoleTransportException>(() => transport.KeyboardAsync("child", new(KeyboardInputKind.Text, "helloé", null, null, null), default));
        await Assert.ThrowsAsync<ConsoleTransportException>(() => transport.KeyboardAsync("child", new(KeyboardInputKind.Scancodes, null, null, null, [0x1e, 0xe0]), default));
        Assert.Empty(runner.Calls);
    }
    [Fact]
    public async Task Absolute_mouse_uses_native_pixels_and_QMP_full_range()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources).RespondStdout("{\"tablet\":1}"); Capture(runner); runner.Respond(Ack);
        Assert.True((await new ProxmoxConsoleTransport(runner, Options()).MouseAsync("child", new(MouseInputKind.MoveAbsolute, 1, 0, null, null, null), default)).Applied);
        var events = Events(runner.Calls[3]); Assert.Equal("abs", events[0].GetProperty("type").GetString());
        Assert.Equal(32767, events[0].GetProperty("data").GetProperty("value").GetInt32());
        Assert.Equal(0, events[1].GetProperty("data").GetProperty("value").GetInt32());
    }
    [Fact]
    public async Task Missing_tablet_returns_relative_fallback_without_input()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources).RespondStdout("{\"tablet\":0}");
        var result = await new ProxmoxConsoleTransport(runner, Options()).MouseAsync("child", new(MouseInputKind.MoveAbsolute, 1, 0, null, null, null), default);
        Assert.False(result.Applied); Assert.Equal("moveRelative", result.Fallback); Assert.Equal(2, runner.Calls.Count);
    }
    [Theory]
    [InlineData(MouseInputKind.Click, 2)]
    [InlineData(MouseInputKind.Press, 1)]
    [InlineData(MouseInputKind.Release, 1)]
    [InlineData(MouseInputKind.MoveRelative, 2)]
    public async Task Mouse_buttons_and_relative_motion_use_typed_events(MouseInputKind kind, int count)
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources).Respond(Ack);
        var input = kind == MouseInputKind.MoveRelative ? new MouseInput(kind, null, null, -4, 5, null) : new(kind, null, null, null, null, 2);
        await new ProxmoxConsoleTransport(runner, Options()).MouseAsync("child", input, default);
        var events = Events(runner.Calls[1]); Assert.Equal(count, events.GetArrayLength());
        if (kind == MouseInputKind.MoveRelative) Assert.Equal(-4, events[0].GetProperty("data").GetProperty("value").GetInt32());
        else { Assert.Equal("right", events[0].GetProperty("data").GetProperty("button").GetString());
            Assert.Equal(kind != MouseInputKind.Release, events[0].GetProperty("data").GetProperty("down").GetBoolean()); }
    }
    [Fact]
    public async Task Native_noVNC_link_uses_node_and_vmid_and_does_not_issue_root_credentials()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources); var interactive = new ProxmoxInteractiveConsole(runner, Options());
        Assert.Equal("https://pve.example.test:8006/?console=kvm&novnc=1&vmid=101&node=pve1&resize=off", await interactive.GetLaunchUrlAsync("child", default));
        await Assert.ThrowsAsync<NotSupportedException>(() => interactive.ConnectAsync(new("id", "child", "owner", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 2, 1), default));
        Assert.Single(runner.Calls);
    }
    [Fact]
    public async Task Unsupported_console_transport_returns_coded_409()
    {
        using var app = new TestApp(configureServices: services => services.AddSingleton<IConsoleTransport, UnsupportedConsoleTransport>());
        using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm");
        using var response = await owner.GetAsync("/api/v1/vms/probe-vm/console/capabilities");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("unsupported-capability", (await response.ReadAsync<JsonElement>()).GetProperty("code").GetString());
    }
    [Fact]
    public async Task Native_link_is_in_session_but_VMConnect_connection_returns_409()
    {
        var runner = new RecordingProcessRunner().RespondStdout(Resources.Replace("child", "probe-vm"));
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:BrowserConsoleEnabled"] = "true" },
            services => services.AddSingleton<IInteractiveConsole>(new ProxmoxInteractiveConsole(runner, Options())));
        using var owner = await app.CreateUserClientAsync("owner"); await owner.CreateVmAsync("probe-vm");
        using var response = await owner.PostJsonAsync("/api/v1/vms/probe-vm/console/sessions", new { });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var session = await response.ReadAsync<JsonElement>();
        Assert.Contains("vmid=101", session.GetProperty("interactiveUrl").GetString());
        Assert.Contains("Proxmox login", session.GetProperty("capabilities").GetProperty("interactiveReason").GetString());
        var id = session.GetProperty("sessionId").GetString();
        using var connection = await owner.PostJsonAsync($"/api/v1/vms/probe-vm/console/sessions/{id}/connection", new { });
        Assert.Equal(HttpStatusCode.Conflict, connection.StatusCode);
        Assert.Equal("unsupported-capability", (await connection.ReadAsync<JsonElement>()).GetProperty("code").GetString());
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task QMP_helper_handles_greeting_events_and_errors_on_a_local_fake_socket(bool fail, bool success)
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("qmp-test-"); var path = Path.Combine(dir.FullName, "socket");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path)); listener.Listen(1);
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptAsync(deadline.Token); using var stream = new NetworkStream(connection);
                using var reader = new StreamReader(stream); using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("{\"QMP\":{}}");
                foreach (var expected in new[] { "qmp_capabilities", "query-status", "input-send-event" })
                {
                    var command = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!).RootElement;
                    Assert.Equal(expected, command.GetProperty("execute").GetString());
                    await writer.WriteLineAsync("{\"event\":\"RESET\"}");
                    var payload = expected == "query-status" ? "{\"status\":\"running\"}" : "{}";
                    await writer.WriteLineAsync("{\"id\":" + command.GetProperty("id").GetRawText() + ",\"" + (fail && expected == "input-send-event" ? "error" : "return") + "\":" + payload + "}");
                }
            }, deadline.Token);
            var result = await new ProcessRunner().RunAsync("python3", ["-c", ProxmoxQmp.Script, path],
                """{"running":true,"commands":[{"execute":"input-send-event","arguments":{"events":[]}}]}""", TimeSpan.FromSeconds(10), null, deadline.Token);
            await server; Assert.Equal(success, result.Succeeded); Assert.Equal(success ? "[{}]" : "[]", result.StandardOutput.Trim());
            Assert.Equal("", result.StandardError);
        }
        finally { listener.Dispose(); dir.Delete(true); }
    }
}
