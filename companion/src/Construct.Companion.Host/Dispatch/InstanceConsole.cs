using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;

namespace Construct.Companion.Host.Dispatch;

public sealed class InstanceConsole(IProcessRunner processes, IPrompts prompts, ILauncher launcher, IClock clock, IpcEvents events)
{
    public async Task OpenAsync(CompanionInstance entry, CancellationToken ct)
    {
        string? error = null;
        try { await OpenCoreAsync(entry, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (InvalidOperationException e) { error = e.Message; }
        catch { error = "Console access could not be prepared. Check the VM connection and local console setup."; }
        finally {
            events.Message(entry.Name, new { type = "lifecyclePrepared", id = "openConsole", error });
            if (error is not null) events.Companion(new { type = "notification", level = "warning", text = error });
        }
    }
    private async Task OpenCoreAsync(CompanionInstance entry, CancellationToken ct)
    {
        var support = GuestConsole.StateFor(entry.Definition, OperatingSystem.IsWindows());
        if (StateJson.Boolean(support["supported"]) != true) throw new InvalidOperationException(StateJson.String(support["reason"]));
        var ensured = await entry.Ssh.RunRemoteScriptAsync(GuestConsole.BuildEnsureGatewayScript(), TimeSpan.FromMinutes(5), ct);
        if (ensured.Code != 0 || GuestConsole.ParseEnsureOutput(ensured.Stdout) is not ("ready" or "installed")) throw new InvalidOperationException(GuestConsole.MapFailure("ensure", ensured));
        Secret? input = null;
        var local = StateJson.String(entry.Definition["backend"]) != "hyperv-remote";
        if (local)
        {
            var directory = entry.Store.ScriptsDirectory ?? throw new InvalidOperationException("Update Construct on this PC to install the console setup script.");
            var vmName = StateJson.String(entry.Definition["vmName"]);
            async Task<JsonObject> Broker()
            {
                ProcessResult result;
                try { result = await processes.RunAsync(LocalConsole.BuildHandoffLaunch(directory, entry.Name, vmName), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { throw new InvalidOperationException("Console access could not be prepared on this PC. Check that Windows PowerShell is available."); }
                if (result.Code != 0) throw new InvalidOperationException("Console access could not be prepared on this PC. Run Set-AgentVmConsoleAccess.ps1 to repair access.");
                return GuestConsole.ParseHandoff(result.Stdout);
            }
            var handoff = await Broker();
            if (StateJson.Boolean(handoff["setupRequired"]) == true)
            {
                if (!await prompts.ConfirmAsync("Set up console access", $"Console access for {entry.Name} is not set up on this PC. Set it up now? Windows asks for administrator approval once.", ct)) throw new InvalidOperationException("Console setup was cancelled. Click Console to try again.");
                await launcher.LaunchElevatedAsync(LocalConsole.BuildSetupLaunch(directory, entry.Name, vmName, StateJson.String(handoff["reason"]) == "credential-out-of-sync"), ct);
                for (var i = 0; i < 60 && StateJson.Boolean(handoff["setupRequired"]) == true; i++) { await clock.DelayAsync(TimeSpan.FromSeconds(2), ct); handoff = await Broker(); }
            }
            if (StateJson.Boolean(handoff["setupRequired"]) == true || handoff["error"] is not null) throw new InvalidOperationException(GuestConsole.MapFailure("broker", new(0), handoff));
            input = new Secret(handoff.ToJsonString() + "\n");
        }
        var link = await GuestConsole.MintLiveLinkAsync(entry.Ssh, GuestConsole.BuildSelfConsoleScript(local), input, ct);
        try { await launcher.OpenAsync(link, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("The browser could not open the console link"); }
    }
}
