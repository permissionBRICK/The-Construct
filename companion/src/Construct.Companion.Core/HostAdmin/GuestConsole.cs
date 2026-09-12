using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Sockets;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.HostAdmin;

public static class GuestConsole
{
    public static JsonObject StateFor(JsonObject instance, bool windows)
    {
        var backend = StateJson.String(instance["backend"]);
        var supported = backend == "hyperv-remote" || ((backend == "hyperv-local" || backend.Length == 0) && windows);
        return supported ? new() { ["supported"] = true } : new() { ["supported"] = false, ["reason"] = backend is "hyperv-local" or "" ? "Console is available from Windows only" : "Console needs a Hyper-V VM on this PC or a Construct host service" };
    }
    public static string BuildSelfConsoleScript(bool local) => "construct vm console self --web" + (local ? " --connection-stdin" : "") + "\n";
    public static string BuildEnsureGatewayScript() => GuestScripts.Render("console-ensure");
    public static string BuildCloseForwardScript(int port) => port is > 0 and <= 65535 ? GuestScripts.Render("console-close", new Dictionary<string, string> { ["clientPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : throw new ArgumentException("Invalid console port");
    public static string ParseEnsureOutput(string stdout) => Regex.Match(stdout, @"^CONSOLE_GATEWAY=(ready|installed|no-docker|missing-source|install-failed)\r?$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "install-failed";
    public static JsonObject ParseHandoff(string stdout)
    {
        JsonObject? value;
        try { value = JsonNode.Parse(stdout) as JsonObject; }
        catch { throw new InvalidOperationException("Console access could not be prepared on this PC. Update Construct and retry."); }
        if (value is not null && StateJson.Boolean(value["setupRequired"]) == true && StateJson.String(value["reason"]) is "no-credential" or "grant-missing" or "credential-out-of-sync") return new() { ["setupRequired"] = true, ["reason"] = value["reason"]!.DeepClone() };
        if (value is not null && StateJson.String(value["error"]) is "vm-not-running" or "vmconnect-unreachable") return new() { ["error"] = value["error"]!.DeepClone() };
        var patterns = new Dictionary<string, string> {
            ["vmId"] = @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", ["username"] = @"[A-Za-z0-9_-]{1,20}",
            ["domain"] = @"[A-Za-z0-9_.-]{1,255}", ["password"] = @"[^\x00-\x1f\x7f]{1,256}", ["certificateFingerprint"] = @"sha256:(?:[0-9a-f]{2}:){31}[0-9a-f]{2}"
        };
        var address = StateJson.Text(value?["hostAddress"]);
        if (value is null || patterns.Any(p => StateJson.Text(value[p.Key]) is not {} text || !Regex.IsMatch(text, @"\A" + p.Value + @"\z")) || address is null || (address.Length > 0 && (!Regex.IsMatch(address, @"\A(?:[0-9]{1,3}\.){3}[0-9]{1,3}\z") || !IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)))
            throw new InvalidOperationException("Console access could not be prepared on this PC. The credential broker returned invalid connection data.");
        return new JsonObject(patterns.Keys.Concat(["hostAddress", "rotated"]).Select(k => new KeyValuePair<string, JsonNode?>(k, value[k]?.DeepClone())));
    }
    public static string MapFailure(string step, ProcessResult result, JsonObject? handoff = null)
    {
        if (result.Code is -1 or -2 || result.Stderr.Contains("ssh:", StringComparison.OrdinalIgnoreCase)) return "The VM did not answer over SSH. Start or connect the VM, then click Console again.";
        var status = step == "ensure" ? ParseEnsureOutput(result.Stdout) : StateJson.String(handoff?["error"]);
        var message = status switch {
            "no-docker" => "Docker is not installed on the VM, so the console gateway cannot run. Reprovision the VM, or make this PC a Construct host.",
            "missing-source" => "The Construct checkout is missing on the VM (/opt/construct/repo). Reprovision the VM.",
            "install-failed" => "Installing the console gateway failed. Run bash /opt/construct/repo/console-viewer/install.sh on the VM to see the full error.",
            "vm-not-running" => "The VM is not running on this PC's Hyper-V.",
            "vmconnect-unreachable" => "Hyper-V's console service (port 2179) did not answer on this PC. Check that the Hyper-V Virtual Machine Management service is running.", _ => null };
        if (message is not null) return message;
        if (StateJson.Boolean(handoff?["setupRequired"]) == true) return "Console setup did not finish. Check the administrator PowerShell window, then click Console again.";
        if (step == "mint" && result.Code == 6) return "No Construct client is attached to the VM, so the viewer port cannot be forwarded. Connect this window to the VM and retry.";
        if (step == "mint" && result.Code == 9) return "This VM's construct CLI is older than the panel. Reprovision the VM.";
        var known = Regex.Match(result.Stderr, @"Host refused console operation \(HTTP \d{3}\)|Browser console is unavailable on this host\.?|Too many viewer links");
        return known.Success ? known.Value : "Could not create a console link. Check that the primary VM and Construct client are connected.";
    }
    public const string ForwardFailure = "The console forward is not open on this PC. Reconnect to the VM (the Forwards card must show port 6080 as open) and try again.";
    public static async Task<string> MintLiveLinkAsync(ISshTransport ssh, string script, Secret? input, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ProcessResult result;
            try { result = await ssh.RunRemoteScriptAsync(script, TimeSpan.FromSeconds(90), ct, input); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw new InvalidOperationException(MapFailure("mint", new(1))); }
            if (result.Code != 0) throw new InvalidOperationException(MapFailure("mint", result));
            var link = ParseBrowserLink(result.Stdout);
            if (await ssh.ProbeListeningPortAsync(new Uri(link).Port, ct)) return link;
            if (attempt == 0 && (await ssh.RunRemoteScriptAsync(BuildCloseForwardScript(new Uri(link).Port), TimeSpan.FromSeconds(90), ct)).Code != 0) break;
        }
        throw new InvalidOperationException(ForwardFailure);
    }
    public static string BuildConsoleScript(string name)
    {
        if (!Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}\z")) throw new ArgumentException("Invalid guest VM name");
        return $"construct vm console '{name}' --web\n";
    }
    public static string ParseBrowserLink(string? stdout)
    {
        var lines = Regex.Split(StateJson.Trim(stdout ?? ""), @"\r?\n");
        if (lines.Length != 1) throw new InvalidOperationException("The console gateway did not return one browser link");
        if (!Uri.TryCreate(lines[0], UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Fragment.Length <= 1)
            throw new InvalidOperationException("The console gateway returned an invalid browser link");
        return url.AbsoluteUri;
    }
    public static async Task OpenAsync(ISshTransport ssh, ILauncher launcher, string name, CancellationToken ct)
    {
        var link = await MintLiveLinkAsync(ssh, BuildConsoleScript(name), null, ct);
        try { await launcher.OpenAsync(link, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("The browser could not open the console link"); }
    }
}
