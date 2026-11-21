using Construct.Agent.Device;
using Construct.Agent.Engines;
using Construct.Agent.Update;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Construct.Agent.Http;

/// <summary>
/// The device features as routes under <c>/api</c> (5.1, 7.2, 12.1): the Connections page, open actions,
/// the SSH alias, the workspace share, the device's settings and updates. A feature this platform lacks
/// answers 501 <c>not-available</c>.
/// </summary>
public static class DeviceRoutes
{
    public static void Map(RouteGroupBuilder api, DeviceServices device, LocalEngineSetup engineSetup)
    {
        // ---- this device ----

        api.MapGet("/device/features", () => TypedResults.Ok(Features(device, engineSetup))).WithName("GetDeviceFeatures")
            .WithDescription("What this device can do: platform, notifications, microphone, workspace share, start with the OS, whether it can run an engine (engineHost).");

        // ---- the engine on this PC (5.3): the first run's setup; the upgrade runs the same as its engine step ----

        api.MapGet("/engines/local/setup", () => TypedResults.Ok(engineSetup.View())).WithName("GetLocalEngineSetup")
            .WithDescription("Where setting up the engine on this PC stands: idle, running, done (with its engine id), failed (the installer's reason) or restart (Windows must restart to turn on Hyper-V).");

        api.MapPost("/engines/local/setup", () => TypedResults.Accepted("/api/engines/local/setup", engineSetup.Start())).WithName("StartLocalEngineSetup")
            .WithDescription("Sets up the engine on this PC (Windows): the host package of the device's channel, checked against the manifest, installed unattended with one elevation prompt; the token goes into the state. An engine on loopback this agent already holds a sign-in for is taken as it is. Progress as engine-setup events; 501 not-available where the device cannot host one.");

        api.MapPost("/device/restart", async (CancellationToken ct) =>
        {
            await engineSetup.RestartAsync(ct);
            return TypedResults.Accepted("/api/engines/local/setup");
        }).WithName("RestartDevice")
            .WithDescription("Restarts Windows now, after the engine setup said Hyper-V needs it (409 no-restart-needed otherwise). Nothing continues by itself afterwards.");

        api.MapGet("/device/settings", () => TypedResults.Ok(Settings(device))).WithName("GetDeviceSettings");

        api.MapPut("/device/settings", async ([FromBody] DeviceSettingsRequest request, CancellationToken ct) =>
        {
            if (request.Channel is { } channel && AgentUpdater.ManifestUrl(channel, AgentUpdater.DefaultRepository) is null)
                throw new AgentFailure(400, "invalid-channel", "A channel is stable, nightly or an https manifest URL.");
            if (request.StartWithOs is { } start)
            {
                if (device.Platform.StartWithOs is null) throw NotAvailable.Feature("Start with the OS");
                device.Platform.SetStartWithOs(start, device.StartExecutable ?? throw new AgentFailure(409, "not-installed", "The agent's executable is unknown."));
            }

            var before = device.Settings.Read().Channel;
            device.Settings.Update(s => s with { Channel = request.Channel ?? s.Channel, AutoUpdate = request.AutoUpdate ?? s.AutoUpdate });
            // This PC's engine follows the device's channel, so one update moves both (F17).
            if (request.Channel is { } changed && changed != before) await engineSetup.FollowChannelAsync(changed, ct);
            return TypedResults.Ok(Settings(device));
        }).WithName("SetDeviceSettings").WithDescription("Changes what is given: the release channel (this PC's engine follows it), automatic updates, start with the OS.");

        api.MapGet("/device/ssh-key", async (CancellationToken ct) =>
            TypedResults.Ok(new SshKeyView(await device.Ssh.EnsureDeviceKeyAsync("construct-agent", ct), device.Ssh.DeviceKeyPath))).WithName("GetDeviceSshKey")
            .WithDescription("The device's SSH public key (created on first use); a VM must authorize it before forwards, notifications and the open actions reach it.");

        // ---- connections (the Connections page) ----

        api.MapGet("/connections", () => TypedResults.Ok(device.Links.Connections())).WithName("ListConnections")
            .WithDescription("The user's own VMs on every engine: the address this device dials, its local forwards and the microphone.");

        api.MapPost("/connections/refresh", async (CancellationToken ct) =>
        {
            await device.Links.RefreshAsync(ct);
            return TypedResults.Ok(device.Links.Connections());
        }).WithName("RefreshConnections");

        var vm = api.MapGroup("/connections/{engine}/{vm}");

        vm.MapDelete("/forwards/{id}", async (string engine, string vm, string id, CancellationToken ct) =>
        {
            await device.Links.CloseForwardAsync(engine, vm, id, ct);
            return TypedResults.NoContent();
        }).WithName("CloseForward");

        vm.MapPost("/sign-in", async (string engine, string vm, [FromBody] SignInForwardRequest request, CancellationToken ct) =>
            TypedResults.Ok(await device.SignIns.StartAsync(engine, vm, request, ct))).WithName("ForwardSignIn")
            .WithDescription("A coding agent's sign-in without a pasted code (11.8): for the running sign-in `session` of `agent` on one of the user's own VMs, whose link returns to a loopback port on the VM, forwards 127.0.0.1:<port> on this device to the VM and opens the link in the default browser. The forward closes when the sign-in ends or after twenty minutes. 409 `no-browser-sign-in`, `port-taken`, `vm-not-running`; 501 when this device cannot open links.");

        vm.MapDelete("/sign-in", async (string engine, string vm, [FromQuery] string session, CancellationToken ct) =>
        {
            await device.SignIns.StopAsync(engine, vm, session, ct);
            return TypedResults.NoContent();
        }).WithName("EndSignInForward").WithDescription("Closes the VM's sign-in forward when it is the one of sign-in `session`; a newer sign-in's forward stays.");

        vm.MapPut("/mic", async (string engine, string vm, [FromBody] SwitchRequest request, CancellationToken ct) =>
            TypedResults.Ok(await device.Links.SetMicAsync(engine, vm, request.On, ct))).WithName("SetMicrophone")
            .WithDescription("Microphone passthrough to the VM; kept for the VM's next start.");

        vm.MapPut("/ssh-config", (string engine, string vm) =>
        {
            var target = device.Links.Target(engine, vm);
            var write = device.Links.WriteAlias(target);
            return TypedResults.Ok(new[] { write, device.Ssh.WriteVsCodePlatform(target.Alias) });
        }).WithName("WriteSshConfig").WithDescription("Writes the VM's Host alias in ~/.ssh/config and VS Code's remote.SSH.remotePlatform entry.");

        vm.MapDelete("/ssh-config", (string engine, string vm) =>
        {
            var target = device.Links.Target(engine, vm);
            return TypedResults.Ok(new[] { device.Ssh.RemoveAlias(target.Alias), device.Ssh.WriteVsCodePlatform(target.Alias, remove: true) });
        }).WithName("RemoveSshConfig");

        vm.MapPost("/open", async (string engine, string vm, [FromBody] OpenRequest request, CancellationToken ct) =>
            TypedResults.Ok(await device.Actions.OpenAsync(engine, vm, request.What, ct))).WithName("OpenVm")
            .WithDescription("Opens the VM: `t3` (the patched T3 Code desktop app Construct installed here, else T3 Code's pairing link in the browser), `vscode` (Remote-SSH), `ssh` (a terminal).");

        vm.MapPost("/t3-pairing", async (string engine, string vm, CancellationToken ct) =>
            TypedResults.Ok(await device.Actions.T3PairingAsync(engine, vm, ct))).WithName("CreateT3PairingLink")
            .WithDescription("A one-time T3 Code pairing link for T3 Desktop on one of the user's own running VMs (`pairUrl`, `links`, `scopes`). The link carries a one-time token: it is in this answer only.");

        vm.MapGet("/share", async (string engine, string vm, CancellationToken ct) => TypedResults.Ok(await device.Actions.ShareAsync(engine, vm, ct))).WithName("GetShare");

        vm.MapPost("/share", async (string engine, string vm, CancellationToken ct) => TypedResults.Ok(await device.Actions.MapShareAsync(engine, vm, ct))).WithName("MapShare")
            .WithDescription("Maps the workspace share of a VM on this PC to a drive letter (Windows).");

        vm.MapDelete("/share", async (string engine, string vm, CancellationToken ct) =>
        {
            await device.Actions.UnmapShareAsync(engine, vm, ct);
            return TypedResults.NoContent();
        }).WithName("UnmapShare");

        // ---- updates (12.1) ----

        api.MapGet("/update", () => TypedResults.Ok(device.Updater.View())).WithName("GetUpdate")
            .WithDescription("This agent's version and channel, and the version on offer (`available`) after the last check.");

        api.MapPost("/update/check", async (CancellationToken ct) => TypedResults.Ok(await device.Updater.CheckAsync(ct))).WithName("CheckUpdate");

        api.MapPost("/update/install", () => TypedResults.Accepted((string?)null, device.Updater.StartInstall())).WithName("InstallUpdate")
            .WithDescription("Installs the version on offer when the agent is idle; the agent restarts as that version, and starts the previous one again when the new one fails its self-test.");

        api.MapGet("/update/everything", () => TypedResults.Ok(device.Everything.View())).WithName("GetUpdateEverything");

        api.MapPost("/update/everything", () => TypedResults.Accepted((string?)null, device.Everything.Start())).WithName("UpdateEverything")
            .WithDescription("Each engine the user administers updates itself, each engine reprovisions the user's own VMs, then this device updates; the parts and their state, never stored.");

        // ---- notices put away (finding 45): the pop-out and the window hide the same ones ----

        api.MapGet("/notices/dismissed", () => TypedResults.Ok(device.Settings.Read().Dismissed)).WithName("ListDismissedNotices")
            .WithDescription("The notices (a failed job's, by the UI's key) put away on this device, newest last.");

        api.MapPost("/notices/dismissed", (DeviceAgent agent, [FromBody] DismissNoticeRequest request) =>
        {
            if (request.Key is not { Length: > 0 and <= 300 } key) throw new AgentFailure(400, "invalid-request", "key is 1 to 300 characters.");
            var kept = device.Settings.Update(s => s.Dismissed.Contains(key) ? s : s with { Dismissed = [.. s.Dismissed.Append(key).TakeLast(200)] }).Dismissed;
            agent.Events.Publish("notices");
            return TypedResults.Ok(kept);
        }).WithName("DismissNotice").WithDescription("Puts a notice away on every surface of this device; the others hear a `notices` event.");

        // ---- the desktop shell ----

        api.MapPost("/shell/open", ([FromBody] ShellOpenRequest request) =>
        {
            if (device.Shell is not { } shell) throw NotAvailable.Feature("A desktop window");
            if (request.Surface is not ("pop" or "win" or "alone")) throw new AgentFailure(400, "invalid-request", "surface is pop, win or alone.");
            shell.Open(request.Surface, request.Route);
            return TypedResults.NoContent();
        }).WithName("OpenShell").WithDescription("Shows a surface of the desktop shell (a second start of the app hands its request over this way).");
    }

    private static DeviceFeatures Features(DeviceServices device, LocalEngineSetup engineSetup) => new(device.Platform.Name, device.Platform.Notifications is not null,
        device.Platform.Microphone is not null, device.Platform.Shares is not null, device.Platform.StartWithOs is not null, device.Shell is not null, engineSetup.Available);

    private static DeviceSettingsView Settings(DeviceServices device)
    {
        var value = device.Settings.Read();
        return new(value.Channel, value.AutoUpdate, device.Platform.StartWithOs);
    }
}

/// <param name="EngineHost">This device can run an engine (Windows): the first run offers "This PC".</param>
public sealed record DeviceFeatures(string Platform, bool Notifications, bool Microphone, bool Share, bool StartWithOs, bool Shell, bool EngineHost);

/// <param name="StartWithOs">Null where the platform cannot start the agent with the session.</param>
public sealed record DeviceSettingsView(string Channel, bool AutoUpdate, bool? StartWithOs);

public sealed record DeviceSettingsRequest(string? Channel = null, bool? AutoUpdate = null, bool? StartWithOs = null);

public sealed record SshKeyView(string PublicKey, string Path);

public sealed record SwitchRequest(bool On);

public sealed record OpenRequest(string What);

public sealed record ShellOpenRequest(string Surface, string? Route = null);

public sealed record DismissNoticeRequest(string Key);
