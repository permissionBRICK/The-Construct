using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Tests.Support;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;

namespace Construct.Agent.Tests;

/// <summary>The device's side of the user's VMs: which VMs get a link, local forwards, notifications, the open actions.</summary>
public sealed class DeviceLinkTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-device-" + Guid.NewGuid().ToString("n"));
    private readonly FakeToastRaiser _toasts = new();
    private readonly FakeDevice _device;

    public DeviceLinkTests()
    {
        var engines = new FakeEngines().Add("e1", "https://host.example:7462")
            .On("e1", "GET whoami", """{"name":"alice","role":"user"}""")
            .On("e1", "GET vms", """
                [{"name":"work","owner":"Alice","kind":"primary","state":"running","deleting":false},
                 {"name":"theirs","owner":"bob","kind":"primary","state":"running","deleting":false},
                 {"name":"sleepy","owner":"alice","kind":"primary","state":"off","deleting":false},
                 {"name":"kid","owner":"alice","kind":"child","state":"running","deleting":false}]
                """)
            .On("e1", "GET vms/work/endpoint", """{"sshHost":"host.example","sshPort":2201,"publicHost":"host.example"}""")
            .On("e1", "GET vms/work/forwards", """[{"id":"web","vmPort":3000,"label":"dev server","target":"client"}]""")
            .On("e1", "GET vms/work/forwards?via=work", "[]")
            .On("e1", "POST vms/work/forwards/web/ack", _ => new JsonObject())
            .On("e1", "DELETE vms/work/forwards/web", _ => null);
        _device = new FakeDevice(_directory, engines, new FakePlatform { Notifications = _toasts });
    }

    [Fact]
    public async Task Only_the_users_own_primaries_are_listed_and_only_running_ones_linked_over_the_engines_endpoint()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);

        var connections = _device.Links.Connections();
        Assert.Equal(["sleepy", "work"], connections.Select(c => c.Vm));
        var work = connections.Single(c => c.Vm == "work");
        Assert.True(work.Linked);
        Assert.Equal(new VmAddress("host.example", 2201, "forwarded"), work.Address);
        Assert.False(connections.Single(c => c.Vm == "sleepy").Linked);
        // Nothing was asked about another user's VM or the child.
        Assert.DoesNotContain(_device.Engines.CallsTo("e1"), c => c.Contains("theirs", StringComparison.Ordinal) || c.Contains("kid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_forward_request_opens_a_loopback_tunnel_is_acked_with_its_port_and_closes_when_the_vm_stops()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        await FakeDevice.WaitAsync(() => _device.Engines.Calls.Any(c => c.Call == "POST vms/work/forwards/web/ack"), "the ack");

        var ack = _device.Engines.Calls.First(c => c.Call == "POST vms/work/forwards/web/ack").Body!;
        Assert.Equal("open", ack["status"]!.GetValue<string>());
        var port = ack["localPort"]!.GetValue<int>();
        var tunnel = Assert.Single(_device.Transports["e1/work"].Tunnels);
        Assert.Equal("127.0.0.1", tunnel.Spec.BindHost);
        Assert.Equal((port, 3000), (tunnel.Spec.LocalPort, tunnel.Spec.VmPort));
        await FakeDevice.WaitAsync(() => _device.Links.Connections().Single(c => c.Vm == "work").Forwards.Any(f => f.Status == "open"), "the open forward");
        var forward = _device.Links.Connections().Single(c => c.Vm == "work").Forwards.Single();
        Assert.Equal(("web", "dev server", $"http://localhost:{port}/"), (forward.Id, forward.Label, forward.Url));

        // The VM stops: its link goes, and with it the tunnel.
        _device.Engines.On("e1", "GET vms", """[{"name":"work","owner":"alice","kind":"primary","state":"off","deleting":false}]""");
        await _device.Links.RefreshAsync(CancellationToken.None);

        Assert.False(_device.Links.Connections().Single().Linked);
        Assert.True(_device.Processes.Sessions.Where(s => s.Child == tunnel.Process).All(s => s.Disposed));
        Assert.True(tunnel.Process.Stopped);
    }

    [Fact]
    public async Task Closing_a_forward_deletes_it_at_the_engine()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        await FakeDevice.WaitAsync(() => _device.Transports["e1/work"].Tunnels.Count == 1, "the tunnel");

        await _device.Links.CloseForwardAsync("e1", "work", "web", CancellationToken.None);

        Assert.Contains("DELETE vms/work/forwards/web", _device.Engines.CallsTo("e1"));
        Assert.True(_device.Transports["e1/work"].Tunnels[0].Process.Stopped);
    }

    [Fact]
    public async Task An_engine_that_does_not_answer_keeps_its_links()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        _device.Engines.On("e1", "GET vms", _ => throw new AgentFailure(502, "engine-unreachable", "down"));

        await _device.Links.RefreshAsync(CancellationToken.None);

        Assert.True(_device.Links.Connections().Single(c => c.Vm == "work").Linked);
    }

    [Fact]
    public async Task Construct_notify_lines_from_the_vm_become_desktop_notifications()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        var watch = _device.Processes.Sessions.Single(s => _device.Transports["e1/work"].Watches.Any(w => w.Process == s.Child));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await watch.EmitAsync($$"""
            {"ts":{{now}},"level":"warn","title":"Build","body":"Tests <failed> & stopped","source":"ci"}
            {"ts":1,"level":"info","title":"Old","body":"an hour too late"}
            not json
            {"ts":{{now}},"title":"","body":""}

            """);

        var toast = Assert.Single(_toasts.Toasts);
        Assert.Contains("<text>Build</text><text>Tests &lt;failed&gt; &amp; stopped</text>", toast.Xml, StringComparison.Ordinal);
        Assert.Contains("duration=\"long\"", toast.Xml, StringComparison.Ordinal);
        Assert.Contains("launch=\"construct://open?instance=work\"", toast.Xml, StringComparison.Ordinal);
        Assert.Equal(["-a", "The Construct", "-u", "critical", "--", "Build", "Tests <failed> & stopped (ci)"], LinuxPlatform.NotifySend.Arguments(toast));
    }

    [Fact]
    public async Task Open_actions_write_the_alias_and_open_vscode_or_a_terminal()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        var actions = new VmActions(_device.Links, _device.Platform);

        await actions.OpenAsync("e1", "work", "vscode", CancellationToken.None);
        await actions.OpenAsync("e1", "work", "ssh", CancellationToken.None);

        Assert.Equal(["vscode://vscode-remote/ssh-remote+work/root/repos", "terminal:work"], _device.Platform.Opened);
        var config = File.ReadAllText(Path.Combine(_device.Platform.Home, ".ssh", "config"));
        Assert.Contains("Host work", config, StringComparison.Ordinal);
        Assert.Contains("    HostName host.example", config, StringComparison.Ordinal);
        Assert.Contains("    Port 2201", config, StringComparison.Ordinal);
        Assert.Contains("    AddressFamily inet", config, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AgentFailure>(() => actions.OpenAsync("e1", "theirs", "vscode", CancellationToken.None));
    }

    [Fact]
    public async Task T3_code_opens_the_first_pairing_link_the_vm_gives_and_says_when_its_forward_is_not_ready()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        var ssh = _device.Transports["e1/work"];
        var actions = new VmActions(_device.Links, _device.Platform);
        ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0,
            """{"links":[{"kind":"forwarded","pairUrl":"https://host.example:2300/pair#token=x"},{"kind":"direct","pairUrl":"http://work.mshome.net:5177/pair#token=x"}]}"""));

        var opened = await actions.OpenAsync("e1", "work", "t3", CancellationToken.None);

        Assert.Equal("forwarded", opened.Kind);
        Assert.Equal("https://host.example:2300/pair#token=x", _device.Platform.Opened.Single());
        ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(7));
        var failure = await Assert.ThrowsAsync<AgentFailure>(() => actions.OpenAsync("e1", "work", "t3", CancellationToken.None));
        Assert.Equal("t3-forward-not-ready", failure.Code);
    }

    [Fact]
    public async Task T3_desktop_gets_a_pairing_link_for_its_own_vm_with_the_administrative_scopes_and_nothing_opens()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        var ssh = _device.Transports["e1/work"];
        var actions = new VmActions(_device.Links, _device.Platform);
        string? script = null;
        ssh.ScriptHandler = (text, _) =>
        {
            script = text;
            return Task.FromResult(new ProcessResult(0, "CONSTRUCT_PAIRING_SCOPES=administrative\n" +
                """{"pairUrl":"https://host.example:2300/pair#token=one","links":[{"kind":"proxy","pairUrl":"https://t3.example:8443/pair#token=two"},{"kind":"forwarded","pairUrl":"https://host.example:2300/pair#token=one"}]}"""));
        };

        var link = await actions.T3PairingAsync("e1", "work", CancellationToken.None);

        Assert.Equal("https://host.example:2300/pair#token=one", link.PairUrl);
        Assert.Equal("administrative", link.Scopes);
        Assert.Equal(["proxy", "forwarded"], link.Links.Select(l => l.Kind));
        Assert.Empty(_device.Platform.Opened);
        Assert.Contains("--label 'construct-t3-desktop-work' $extra", script, StringComparison.Ordinal);
        // The script parses as bash.
        var check = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("bash", "-n") { RedirectStandardInput = true, RedirectStandardError = true })!;
        await check.StandardInput.WriteAsync(script);
        check.StandardInput.Close();
        await check.WaitForExitAsync();
        Assert.True(check.ExitCode == 0, await check.StandardError.ReadToEndAsync());
        Assert.Contains("--scopes administrative", script, StringComparison.Ordinal);
        Assert.Contains("t3pair()", script, StringComparison.Ordinal);

        // A stock T3 without --scopes; then a VM whose T3 is missing; another user's VM is never asked.
        ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(0, """{"pairUrl":"http://work.mshome.net:5177/pair#token=x"}"""));
        Assert.Equal("standard", (await actions.T3PairingAsync("e1", "work", CancellationToken.None)).Scopes);
        ssh.ScriptHandler = (_, _) => Task.FromResult(new ProcessResult(2));
        Assert.Equal("t3-not-installed", (await Assert.ThrowsAsync<AgentFailure>(() => actions.T3PairingAsync("e1", "work", CancellationToken.None))).Code);
        Assert.Equal(404, (await Assert.ThrowsAsync<AgentFailure>(() => actions.T3PairingAsync("e1", "theirs", CancellationToken.None))).Status);
    }

    [Fact]
    public async Task On_this_pcs_engine_a_vm_is_dialled_at_the_direct_address_the_engine_names()
    {
        var engines = new FakeEngines().Add("local", "https://127.0.0.1:7462").Add("remote", "https://pc.example:7462");
        foreach (var (engine, vm) in new[] { ("local", "work"), ("remote", "far") })
            engines.On(engine, "GET whoami", """{"name":"alice"}""")
                .On(engine, "GET vms", $$"""[{"name":"{{vm}}","owner":"alice","kind":"primary","state":"running"}]""")
                .On(engine, $"GET vms/{vm}/endpoint", $$$"""{"sshHost":"pc.example","sshPort":2201,"publicHost":"pc.example","forwarded":{"host":"pc.example","port":2201},"direct":{"host":"{{{vm}}}.mshome.net","port":22}}""")
                .On(engine, $"GET vms/{vm}/forwards", "[]").On(engine, $"GET vms/{vm}/forwards?via={vm}", "[]");
        await using var device = new FakeDevice(_directory + "-local", engines, new FakePlatform());

        await device.Links.RefreshAsync(CancellationToken.None);

        // On the engine's own machine the direct address; from elsewhere the forward.
        var connections = device.Links.Connections();
        Assert.Equal(new VmAddress("work.mshome.net", 22, "direct"), connections.Single(c => c.Engine == "local").Address);
        Assert.Equal(new VmAddress("pc.example", 2201, "forwarded"), connections.Single(c => c.Engine == "remote").Address);
    }

    [Fact]
    public async Task Features_this_platform_lacks_answer_not_available()
    {
        await _device.Links.RefreshAsync(CancellationToken.None);
        var actions = new VmActions(_device.Links, _device.Platform);

        Assert.Equal(501, (await Assert.ThrowsAsync<AgentFailure>(() => _device.Links.SetMicAsync("e1", "work", true, CancellationToken.None))).Status);
        Assert.Equal(501, (await Assert.ThrowsAsync<AgentFailure>(() => actions.MapShareAsync("e1", "work", CancellationToken.None))).Status);
    }

    [Fact]
    public void Config_env_values_are_read_as_config_set_writes_them()
    {
        var values = VmActions.ParseConfig("SMB_SHARE=true\nSMB_USER=dev\nSMB_PASSWORD='it'\\''s'\r\nSMB_SHARE_NAME=repo\n");
        Assert.Equal(("true", "dev", "it's", "repo"), (values["SMB_SHARE"], values["SMB_USER"], values["SMB_PASSWORD"], values["SMB_SHARE_NAME"]));
    }

    public async ValueTask DisposeAsync()
    {
        await _device.DisposeAsync();
        foreach (var directory in new[] { _directory, _directory + "-local" })
        {
            try { Directory.Delete(directory, true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
