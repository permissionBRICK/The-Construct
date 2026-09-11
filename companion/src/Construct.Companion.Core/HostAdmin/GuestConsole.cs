using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.HostAdmin;

public static class GuestConsole
{
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
        var script = BuildConsoleScript(name);
        ProcessResult result;
        try { result = await ssh.RunRemoteScriptAsync(script, TimeSpan.FromSeconds(90), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("Could not create a console link. Check that the primary VM and Construct client are connected."); }
        // SSH output and launcher exception text may contain the ticket. Never surface either.
        if (result.Code != 0) throw new InvalidOperationException("Could not create a console link. Check that the primary VM and Construct client are connected.");
        var link = ParseBrowserLink(result.Stdout);
        try { await launcher.OpenAsync(link, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("The browser could not open the console link"); }
    }
}
