using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Remote;
using RemoteHost = Construct.Companion.Core.Remote.RemoteHost;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Fakes;

namespace Construct.Companion.Tests.Desktop;

public sealed class DesktopModelTests
{
    [Theory]
    [InlineData("open", "panel")]
    [InlineData("settings", "settings")]
    [InlineData("unrecognized", "popup")]
    public void UriActivation(string route, string expected) => Assert.Equal(expected, Activation.Resolve(new(Uri: "construct://" + route + "?instance=dev"), ["dev"], []).Views.Single().View);
    [Theory]
    [InlineData("construct://[")]
    [InlineData("construct://open?instance=missing")]
    [InlineData("construct://hostadmin?host=missing")]
    [InlineData("construct://open?instance=dev&instance=dev")]
    [InlineData("construct://forward?instance=dev&id=../secret")]
    [InlineData("construct://forward?id=x")]
    [InlineData("construct://user@open?instance=dev")]
    public void ActivationRejectsUntrustedTargets(string uri) => Assert.Throws<ArgumentException>(() => Activation.Resolve(new(Uri: uri), ["dev"], []));
    [Theory]
    [InlineData("panel", "panel")] [InlineData("settings", "settings")] [InlineData("theme", "theme")]
    public void InProcessActivationsFollowTheCommandLineRules(string view, string expected)
    {
        Assert.Equal(expected, Activation.ResolveView(new UiActivation(view, "dev"), ["dev"], []).Views.Single().View);
        Assert.Throws<ArgumentException>(() => Activation.ResolveView(new UiActivation(view, "missing"), ["dev"], []));
        Assert.Equal("hostadmin", Activation.ResolveView(new UiActivation("hostadmin", Host: "lab"), ["dev"], ["lab"]).Views.Single().View);
    }
    [Fact]
    public void CombinableViewsAndForwardRemainSeparate()
    {
        var plan = Activation.Resolve(new(Panel: true, Settings: true, Instance: "dev", Uri: "construct://forward?instance=dev&id=web"), ["dev"], []);
        Assert.Equal(["panel", "settings"], plan.Views.Select(v => v.View)); Assert.Equal("web", plan.ForwardId);
        Assert.Empty(Activation.Resolve(new(Background: true), [], []).Views);
    }
    [Theory]
    [InlineData(true, "off", false, false, 0, TrayColor.Green)]
    [InlineData(true, "running", false, true, 0, TrayColor.Red)]
    [InlineData(true, "off", true, false, 0, TrayColor.Yellow)]
    [InlineData(false, "running", false, false, 0, TrayColor.Yellow)]
    [InlineData(false, "saved", false, false, 0, TrayColor.Grey)]
    [InlineData(false, "paused", false, false, 0, TrayColor.Grey)]
    [InlineData(false, "absent", false, false, 0, TrayColor.Red)]
    [InlineData(false, "unknown", false, false, 120, TrayColor.Yellow)]
    [InlineData(false, "unknown", false, false, 121, TrayColor.Red)]
    [InlineData(false, "off", false, true, 0, TrayColor.Red)]
    public void TrayStates(bool online, string vm, bool busy, bool error, int seconds, TrayColor expected)
    {
        var appearance = TrayModel.Appearance(new("dev", true, online, vm, busy, error, TimeSpan.FromSeconds(seconds), UpdateAvailable: true));
        Assert.Equal(expected, appearance.Color); Assert.True(appearance.Update); Assert.False(appearance.Question);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ClickingVisiblePopupClosesRegardlessOfDeactivationOrder(bool deactivateFirst)
    {
        var gesture=new PopupGesture();
        if (deactivateFirst) { gesture.FocusLost(true); gesture.Press(false); }
        else { gesture.Press(true); gesture.FocusLost(true); }
        Assert.False(gesture.Click());
        gesture.Press(false); Assert.True(gesture.Click());
    }
    [Fact]
    public void ClickingTrayAfterOrdinaryFocusLossOpensAndDoubleClickResets()
    {
        var gesture=new PopupGesture(); gesture.FocusLost(false); gesture.Press(false); Assert.True(gesture.Click());
        gesture.Press(true); gesture.Reset(); gesture.Press(false); Assert.True(gesture.Click());
    }
    [Fact]
    public void MissingAndLongInstanceTooltipAreSafe()
    {
        Assert.True(TrayModel.Appearance(new()).Question);
        Assert.True(TrayModel.Appearance(new(new string('x', 100), true)).Tooltip.Length <= 63);
        Assert.Equal([16, 20, 24, 32], new[] { 96, 120, 144, 192 }.Select(TrayModel.IconSize));
    }
    [Theory]
    [InlineData(0, 0, "Construct: no VM configured")] [InlineData(0, 2, "All Construct VMs offline")]
    [InlineData(1, 2, "1 Construct VM online")] [InlineData(2, 2, "2 Construct VMs online")]
    public void TooltipCountsOnlineVmsOnly(int online, int total, string expected)
    {
        Assert.Equal(expected, TrayModel.Tooltip(online, total));
        var appearance = TrayModel.Appearance(new("dev", true, Online: true, Mic: true, ForwardCount: 3, OnlineCount: online, InstanceCount: total));
        Assert.Equal(expected, appearance.Tooltip);
        Assert.DoesNotContain("mic", appearance.Tooltip); Assert.False(appearance.Mic);
        var active = TrayModel.Appearance(new("dev", true, Online: true, OnlineCount: online, InstanceCount: total, MicActive: true));
        Assert.Equal(expected + " · mic active", active.Tooltip); Assert.True(active.Mic);
        Assert.Equal("dev · online", TrayModel.StatusLine(new("dev", true, Online: true, Mic: true)));
    }
    [Fact]
    public void SnapshotCountsOnlineInstancesFromTheirStateMessages()
    {
        var snapshot = new DesktopSnapshot(new FakeClock()); snapshot.SetInstances(["a", "b"]); snapshot.Select("a", true);
        snapshot.Apply(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "state", state = new { instance = "a", online = true, vmState = "running" } }));
        snapshot.ApplyOnline("b", true); snapshot.ApplyOnline("zzz", true);
        Assert.Equal((2, 2), (snapshot.Current.OnlineCount, snapshot.Current.InstanceCount));
        snapshot.SetInstances(["a"]); Assert.Equal((1, 1), (snapshot.Current.OnlineCount, snapshot.Current.InstanceCount));
        // The passthrough session being enabled is not "mic active"; only a live capture is.
        snapshot.Apply(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "audio", instance = "a", enabled = true, capturing = false }));
        Assert.False(snapshot.Current.MicActive);
        snapshot.Apply(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "audio", instance = "a", enabled = true, capturing = true }));
        Assert.True(snapshot.Current.MicActive);
        snapshot.ApplyMic("a", false); Assert.False(snapshot.Current.MicActive);
        // A pending reprovision shows a yellow dot; an available update (blue) takes precedence.
        snapshot.Apply(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "state", state = new { instance = "a", online = true, vmState = "running", provisionStale = true } }));
        Assert.True(snapshot.Current.ProvisionStale); Assert.True(TrayModel.Appearance(snapshot.Current).Stale); Assert.False(TrayModel.Appearance(snapshot.Current).Update);
        snapshot.Apply(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "state", state = new { instance = "a", online = true, vmState = "running", provisionStale = true, constructUpdate = new { available = true } } }));
        Assert.True(TrayModel.Appearance(snapshot.Current).Update); Assert.False(TrayModel.Appearance(snapshot.Current).Stale);
        snapshot.Apply(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "state", state = new { instance = "a", online = true, vmState = "running" } }));
        Assert.False(TrayModel.Appearance(snapshot.Current).Stale); Assert.False(TrayModel.Appearance(snapshot.Current).Update);
    }
    [Fact]
    public void MenuGatesAndChecks()
    {
        var menu = TrayModel.Menu(new("dev", true, VmState: "saved", Mic: true), ["dev", "other"], [], true, false);
        Assert.Equal("Resume", menu.Single(m => m.Id == "startVm").Text);
        Assert.True(menu.Single(m => m.Id == "mic").Checked);
        Assert.False(menu.Single(m => m.Id == "autostart").Checked);
        Assert.DoesNotContain(menu, m => m.Id == "hostadmin");
        Assert.True(menu.Single(m => m.Id == "registerVm").Enabled); Assert.True(menu.Single(m => m.Id == "createRemoteVm").Enabled);
        Assert.False(menu.Single(m => m.Id == "forwards").Children!.Single().Enabled);
    }
    [Theory]
    [InlineData(-1900, -400)]
    [InlineData(5000, 9000)]
    public void BoundsStayOnSelectedMonitor(int x, int y)
    {
        var b = WindowPlacement.Clamp(new(x, y, 4000, 3000), new(-1920, 0, 1920, 1080));
        Assert.Equal(new(-1920, 0, 1920, 1080), b);
    }
    [Fact]
    public void PopupAnchorsAboveBottomAndBelowTopTaskbar()
    {
        Assert.Equal(new(1520, 580, 400, 500), WindowPlacement.Popup(new(1880,1080,40,40),new(0,0,1920,1080),400,500));
        Assert.Equal(new(1520,40,400,500),WindowPlacement.Popup(new(1880,0,40,40),new(0,40,1920,1040),400,500));
    }
    [Fact]
    public async Task TokenBase64RoundtripDenialAndTraversal()
    {
        var files = new FakeFileSystem(); var protection = new FakeDataProtection(); var store = new ProtectedTokenStore(files,protection,"/remote");
        var slug=RemoteHost.HostSlug("https://buildbox.example.local:7462");
        await store.WriteAsync(slug,new Secret("fixture dotted token")); Assert.NotNull(await store.ReadAsync(slug));
        var secret = new Secret("fixture token ü"); await store.WriteAsync("host-1",secret);
        Assert.Equal(Convert.ToBase64String(protection.Protect(Encoding.UTF8.GetBytes(secret.Reveal()))),Encoding.UTF8.GetString(files.ReadFile("/remote/host-1.token")!));
        Assert.Equal(secret.Reveal(),(await store.ReadAsync("host-1"))!.Reveal());
        protection.Denied = true; Assert.Null(await store.ReadAsync("host-1"));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.WriteAsync("../host",secret));
    }
    [Fact]
    public async Task LauncherRecordsLifecycleArgvAndT3Environment()
    {
        var files = new FakeFileSystem(); files.Roots[FileSystemRoot.LocalAppData]="/local";
        var desktop = new FakeDesktopProcess(); var launcher = new DesktopLauncher(desktop,files);
        var invocation = PowerShellLaunch.BuildHostLaunch("C:\\scripts with space\\Auto-Install.ps1",["-InstanceName","dev"],true).Invocation();
        await launcher.LaunchElevatedAsync(invocation); Assert.Equal(invocation,desktop.Invocations.Single()); Assert.False(desktop.Invocations.Single().CreateNoWindow);
        await Assert.ThrowsAsync<ArgumentException>(()=>launcher.LaunchElevatedAsync(new("powershell.exe",[])));
        await launcher.OpenAsync("vscode://vscode-remote/ssh-remote+dev/root"); Assert.Equal("vscode://vscode-remote/ssh-remote+dev/root",desktop.Opened.Single());
        Assert.False(await launcher.OpenT3DesktopAsync()); files.WriteFileAtomic("/local/Programs/t3code/Uninstall T3 Code.exe",[]);
        Assert.False(await launcher.OpenT3DesktopAsync()); files.WriteFileAtomic("/local/Programs/t3code/Desktop-App.exe",[]);
        Assert.True(await launcher.OpenT3DesktopAsync()); Assert.True(desktop.Invocations.Last().CreateNoWindow); Assert.EndsWith("Desktop-App.exe",desktop.Invocations.Last().FileName); Assert.Empty(desktop.Invocations.Last().Arguments);
        Assert.Equal("1",desktop.Invocations.Last().EnvironmentOverrides!["ELECTRON_NO_ATTACH_CONSOLE"]);
    }
    [Fact]
    public async Task CimDenialUsesExactParityTestedGetVmArgv()
    {
        var runner = new FakeProcessRunner(); runner.Results.Enqueue(new(0,"VMSTATE=Saved"));
        var query = new HypervisorQuery(new FakeCimVmQuery { Denied = true });
        Assert.Equal("off",await VmPower.QueryLocalAsync(query,runner,"VM 'quoted'"));
        Assert.Single(runner.Invocations);
        var expected = VmPower.BuildStateProbeLaunch("VM 'quoted'").Invocation();
        Assert.Equal(expected.FileName,runner.Invocations.Single().FileName);
        Assert.Equal(expected.Arguments,runner.Invocations.Single().Arguments);
        Assert.Equal(TimeSpan.FromSeconds(15),runner.Invocations.Single().Timeout);
    }
    [Theory]
    [InlineData(2,HypervisorState.Running)] [InlineData(3,HypervisorState.Off)]
    [InlineData(32768,HypervisorState.Paused)] [InlineData(32769,HypervisorState.Saved)] [InlineData(10,HypervisorState.Unknown)]
    public void CimStateMapping(int state, HypervisorState expected) => Assert.Equal(expected,HypervisorQuery.Map(new((ushort)state)));
    [Fact]
    public void RegistrationIsPerUserAndAutostartIndependent()
    {
        var registry = new FakeRegistry(); var registration = new DesktopRegistration(registry,"C:\\app space\\ConstructCompanion.exe");
        Assert.False(registration.ToastRegistered); Assert.False(registration.Autostart);
        registry.WriteString(DesktopRegistration.ToastKey,"DisplayName","Construct Companion"); registry.WriteString(DesktopRegistration.ToastKey,"IconUri","C:\\app space\\icon.ico");
        Assert.True(registration.ToastRegistered);
        registration.SetAutostart(true); Assert.True(registration.Autostart);
        Assert.Equal("\"C:\\app space\\ConstructCompanion.exe\" --background", registry.ReadString(DesktopRegistration.RunKey,"ConstructCompanion"));
        registration.SetAutostart(false); Assert.False(registration.Autostart);
    }
}
