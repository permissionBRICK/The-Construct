using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Fakes;

namespace Construct.Companion.Tests;

public sealed class FakesTests
{
    [Fact]
    public async Task ProcessLifetimeCancellationClosesStreamsAndCompletion()
    {
        var runner = new FakeProcessRunner(); using var cancel = new CancellationTokenSource();
        await using var process = runner.Start(new ProcessInvocation("tool", []), cancel.Token);
        runner.Processes[0].Emit("first"); cancel.Cancel();
        var chunks = new List<string>(); await foreach (var chunk in process.StandardOutput) chunks.Add(chunk);
        Assert.Equal(["first"], chunks); Assert.Equal(0, await process.Completion); Assert.True(runner.Processes[0].Stopped);
    }

    [Fact]
    public async Task ScriptedSpoolDrivesReconcileAckAndClose()
    {
        var ssh = new FakeSshTransport();
        ssh.Spool.Requests["demo"] = "request";
        ssh.Spool.ExpectRun("reconcile", spool => new ProcessResult(0, spool.Requests["demo"]));
        ssh.Spool.ExpectRun("ack", spool => { spool.Acks["demo"] = "ack"; return new(0); });
        ssh.Spool.ExpectRun("close", spool => { spool.Requests.Remove("demo"); spool.Acks.Remove("demo"); return new(0); });
        Assert.Equal("request", (await ssh.RunRemoteScriptAsync("reconcile")).Stdout);
        await using var tunnel = ssh.SpawnTunnel(new(5173, 5173));
        await ssh.RunRemoteScriptAsync("ack");
        Assert.Single(ssh.Spool.Acks);
        await ssh.RunRemoteScriptAsync("close");
        Assert.Empty(ssh.Spool.Requests); Assert.Empty(ssh.Spool.Acks);
        Assert.Equal(0, ssh.Spool.RemainingSteps);
        await tunnel.StopAsync(); Assert.True(ssh.Tunnels[0].Process.Stopped);
    }

    [Fact]
    public async Task SpoolNotificationsAreDrainedOnlyOnceAndWatchCanExit()
    {
        var ssh = new FakeSshTransport();
        await using var watch = ssh.SpawnWatch("notify-watch");
        ssh.Spool.Notifications.Enqueue("toast");
        var process = ssh.Watches[0].Process;
        ssh.Spool.DrainNotifications(process); ssh.Spool.DrainNotifications(process); process.Exit(12);
        var output = new List<string>(); await foreach (var line in watch.StandardOutput) output.Add(line);
        Assert.Equal(["toast\n"], output); Assert.Equal(12, await watch.Completion);
    }

    [Fact]
    public async Task PortProbeIsScopedToBindAddress()
    {
        var ssh = new FakeSshTransport(); ssh.BusyPorts.Add((5173, "0.0.0.0"));
        Assert.True(await ssh.ProbePortAsync(5173)); Assert.False(await ssh.ProbePortAsync(5173, "0.0.0.0"));
    }

    [Fact]
    public async Task ClockAdvancesDelaysAndSupportsCancellation()
    {
        var clock = new FakeClock(); using var cancel = new CancellationTokenSource();
        var due = clock.DelayAsync(TimeSpan.FromSeconds(5)); var cancelled = clock.DelayAsync(TimeSpan.FromSeconds(10), cancel.Token);
        clock.Advance(TimeSpan.FromSeconds(4)); Assert.False(due.IsCompleted);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        clock.Advance(TimeSpan.FromSeconds(1)); await due;
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(5), clock.UtcNow);
    }

    [Fact]
    public void FilesAreCopiedAndWatchesUnsubscribe()
    {
        var fs = new FakeFileSystem(); fs.Roots[FileSystemRoot.LocalAppData] = "C:/data";
        Assert.Equal("C:/data", fs.GetRoot(FileSystemRoot.LocalAppData));
        var calls = 0; using (fs.Watch("C:/data", () => calls++))
        {
            fs.WriteFileAtomic("C:/data/state.json", [1, 2]);
            var copy = fs.ReadFile("C:/data/state.json")!; copy[0] = 9;
            Assert.Equal(new byte[] { 1, 2 }, fs.ReadFile("C:/data/state.json"));
            Assert.Single(fs.EnumerateFiles("C:/data"));
        }
        fs.DeleteFile("C:/data/state.json"); Assert.Equal(1, calls); Assert.False(fs.FileExists("C:/data/state.json"));
    }

    [Fact]
    public async Task PinIsVerifiedBeforeRequestIsRecorded()
    {
        var api = new FakeRemoteApi(); var request = new RemoteRequest("GET", new Uri("https://host/"), _ => false, Token: new Secret("private-value"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.SendAsync(request)); Assert.Empty(api.Requests);
        api.Responses.Enqueue(new(200)); Assert.Equal(200, (await api.SendAsync(request with { VerifyPin = p => p == api.Fingerprint })).StatusCode);
    }

    [Fact]
    public async Task HypervisorKeepsSavedDistinct()
    {
        var hypervisor = new FakeHypervisorState(); hypervisor.States["dev"] = HypervisorState.Saved;
        Assert.Equal(HypervisorState.Saved, await hypervisor.QueryAsync("dev"));
        Assert.Equal(HypervisorState.Unknown, await hypervisor.QueryAsync("missing"));
    }

    [Fact]
    public async Task PromptsCoverInputSingleMultiConfirmAndSave()
    {
        var prompts = new FakePrompts(); prompts.Inputs.Enqueue("answer"); prompts.Picks.Enqueue(["one"]); prompts.Picks.Enqueue(["one", "two"]); prompts.Confirmations.Enqueue(true); prompts.SaveFiles.Enqueue(null);
        Assert.Equal("answer", await prompts.InputAsync(new("title", "question")));
        Assert.Equal(["one"], await prompts.PickAsync(new("title", [new("one", "One")])));
        Assert.Equal(["one", "two"], await prompts.PickAsync(new("title", [new("one", "One"), new("two", "Two")], true)));
        Assert.True(await prompts.ConfirmAsync("title", "question")); Assert.Null(await prompts.SaveFileAsync(new("title"))); Assert.Equal(5, prompts.Shown.Count);
    }

    [Fact]
    public async Task CaptureReleasesOnEarlyDispose()
    {
        var audio = new FakeAudioCapture(); audio.Devices.Add(new("default", "Mic", true)); audio.Frames.Add([1, 2]); audio.Frames.Add([3, 4]);
        Assert.Single(await audio.EnumerateDevicesAsync());
        await using (var capture = audio.CaptureAsync("default").GetAsyncEnumerator())
        {
            Assert.True(await capture.MoveNextAsync()); Assert.Equal(1, audio.ActiveCaptures); Assert.Equal(new byte[] { 1, 2 }, capture.Current.ToArray());
        }
        Assert.Equal(0, audio.ActiveCaptures);
    }

    [Fact]
    public async Task ToastAndLaunchSeamsRecordCalls()
    {
        var toast = new FakeToastRaiser(); Assert.Equal(ToastAvailability.Available, await toast.GetAvailabilityAsync()); await toast.RaiseAsync(new("<toast/>")); Assert.Single(toast.Toasts);
        var launcher = new FakeLauncher(); var command = new ProcessInvocation("tool.exe", ["argument with spaces"]);
        await launcher.OpenAsync("construct://open"); await launcher.LaunchElevatedAsync(command); await launcher.StartDetachedAsync(command);
        Assert.Equal(command, launcher.Elevated.Single()); Assert.Equal(command, launcher.Detached.Single()); Assert.Equal("construct://open", launcher.Opened.Single());
    }

    [Fact]
    public void RegistryHandlesDefaultValuesAndCaseInsensitiveTrees()
    {
        var registry = new FakeRegistry(); registry.WriteString(@"Software\Classes\construct", null, "URL"); registry.WriteString(@"Software\Classes\construct\shell", "command", "exe");
        Assert.Equal("URL", registry.ReadString(@"software\classes\construct", ""));
        registry.DeleteValue(@"Software\Classes\construct\shell", "command"); Assert.Null(registry.ReadString(@"Software\Classes\construct\shell", "command"));
    }

    [Fact]
    public async Task TokensAreStoredButNeverPrintedByRecords()
    {
        var store = new FakeTokenStore(); var secret = new Secret("private-value"); await store.WriteAsync("host", secret);
        Assert.Same(secret, await store.ReadAsync("host"));
        Assert.DoesNotContain(secret.Reveal(), new ProcessInvocation("ssh", [], StandardInput: secret).ToString());
        Assert.DoesNotContain(secret.Reveal(), new Endpoint(1, 123, secret.Reveal(), 1, DateTimeOffset.UnixEpoch, "test").ToString());
        Assert.Equal("[redacted]", secret.ToString()); await store.DeleteAsync("host"); Assert.Null(await store.ReadAsync("host"));
    }
}
