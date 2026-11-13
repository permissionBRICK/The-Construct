using Construct.Agent.Device;
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
    public static void Map(RouteGroupBuilder api, DeviceServices device)
    {
        // ---- this device ----

        api.MapGet("/device/features", () => TypedResults.Ok(Features(device))).WithName("GetDeviceFeatures")
            .WithDescription("What this device can do: platform, notifications, microphone, workspace share, start with the OS (null: not available).");

        api.MapGet("/device/settings", () => TypedResults.Ok(Settings(device))).WithName("GetDeviceSettings");

        api.MapPut("/device/settings", ([FromBody] DeviceSettingsRequest request) =>
        {
            if (request.Channel is { } channel && AgentUpdater.ManifestUrl(channel, AgentUpdater.DefaultRepository) is null)
                throw new AgentFailure(400, "invalid-channel", "A channel is stable, nightly or an https manifest URL.");
            if (request.StartWithOs is { } start)
            {
                if (device.Platform.StartWithOs is null) throw NotAvailable.Feature("Start with the OS");
                device.Platform.SetStartWithOs(start, device.StartExecutable ?? throw new AgentFailure(409, "not-installed", "The agent's executable is unknown."));
            }

            device.Settings.Update(s => s with { Channel = request.Channel ?? s.Channel, AutoUpdate = request.AutoUpdate ?? s.AutoUpdate });
            return TypedResults.Ok(Settings(device));
        }).WithName("SetDeviceSettings").WithDescription("Changes what is given: the release channel, automatic updates, start with the OS.");

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
            .WithDescription("Opens the VM: `t3` (T3 Code's pairing link in the browser), `vscode` (Remote-SSH), `ssh` (a terminal).");

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

        // ---- the desktop shell ----

        api.MapPost("/shell/open", ([FromBody] ShellOpenRequest request) =>
        {
            if (device.Shell is not { } shell) throw NotAvailable.Feature("A desktop window");
            if (request.Surface is not ("pop" or "win" or "alone")) throw new AgentFailure(400, "invalid-request", "surface is pop, win or alone.");
            shell.Open(request.Surface, request.Route);
            return TypedResults.NoContent();
        }).WithName("OpenShell").WithDescription("Shows a surface of the desktop shell (a second start of the app hands its request over this way).");
    }

    private static DeviceFeatures Features(DeviceServices device) => new(device.Platform.Name, device.Platform.Notifications is not null,
        device.Platform.Microphone is not null, device.Platform.Shares is not null, device.Platform.StartWithOs is not null, device.Shell is not null);

    private static DeviceSettingsView Settings(DeviceServices device)
    {
        var value = device.Settings.Read();
        return new(value.Channel, value.AutoUpdate, device.Platform.StartWithOs);
    }
}

public sealed record DeviceFeatures(string Platform, bool Notifications, bool Microphone, bool Share, bool StartWithOs, bool Shell);

/// <param name="StartWithOs">Null where the platform cannot start the agent with the session.</param>
public sealed record DeviceSettingsView(string Channel, bool AutoUpdate, bool? StartWithOs);

public sealed record DeviceSettingsRequest(string? Channel = null, bool? AutoUpdate = null, bool? StartWithOs = null);

public sealed record SshKeyView(string PublicKey, string Path);

public sealed record SwitchRequest(bool On);

public sealed record OpenRequest(string What);

public sealed record ShellOpenRequest(string Surface, string? Route = null);
