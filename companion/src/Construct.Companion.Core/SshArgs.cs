using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Construct.Companion.Core;

public sealed record SshConfiguration(string VmHost = "agent-vm.mshome.net", string HostAlias = "agent-vm",
    string User = "root", string KeyName = "agent_vm_ed25519", int SshPort = 22, int ConnectTimeout = 12);

public static partial class SshArgs
{
    public static string[] Build(SshConfiguration cfg, string command, string? keyPath = null)
    {
        var args = Common(cfg);
        AddPort(args, cfg.SshPort);
        return Finish(args, cfg, keyPath).Append(command).ToArray();
    }

    public static string[] BuildLocalForward(SshConfiguration cfg, int localPort, int vmPort,
        string? keyPath = null, string? bindHost = null, string? connectAddress = null, int? connectPort = null)
    {
        if (!ValidPort(localPort) || !ValidPort(vmPort) || !ValidPort(connectPort ?? vmPort))
            throw new ArgumentException("Invalid forward port.");
        var far = NormalizeConnectAddress(connectAddress) ?? throw new ArgumentException("Invalid forward destination.");
        var args = new List<string> { "-N" };
        args.AddRange(Common(cfg));
        args.AddRange(["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ExitOnForwardFailure=yes",
            "-L", $"{NormalizeBindHost(bindHost)}:{localPort}:{far}:{connectPort ?? vmPort}"]);
        AddPort(args, cfg.SshPort);
        return Finish(args, cfg, keyPath);
    }

    public static string WrapScriptCommand(string script)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"f=$(mktemp) && printf %s '{b64}' | base64 -d > \"$f\" && bash \"$f\"; rc=$?; rm -f \"$f\"; exit $rc";
    }
    public static int NormalizeSshPort(int port) => ValidPort(port) ? port : 22;
    public static string NormalizeBindHost(string? host) => ForwardHost.TrimWhitespace(host ?? "") is "0.0.0.0" or "*" or "::" ? "0.0.0.0" : "127.0.0.1";
    public static string? NormalizeConnectAddress(string? value)
    {
        var text = ForwardHost.TrimWhitespace(value ?? "");
        if (text.Length == 0) return "127.0.0.1";
        var bare = text.StartsWith('[') && text.EndsWith(']') ? text[1..^1] : text;
        // ssh.js accepts scoped IPv6 destinations; host labels deliberately do not.
        var percent = bare.IndexOf('%');
        var address = percent < 0 ? bare : bare[..percent];
        var validZone = percent < 0 || Zone().IsMatch(bare[(percent + 1)..]);
        if (validZone && ForwardHost.IsIpv6(address)) return $"[{bare}]";
        return bare.Length <= 253 && HostName().IsMatch(bare) ? bare : null;
    }
    private static bool ValidPort(int port) => port is > 0 and <= 65535;
    private static List<string> Common(SshConfiguration cfg) => ["-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
        "-o", "ConnectTimeout=" + cfg.ConnectTimeout.ToString(CultureInfo.InvariantCulture)];
    private static void AddPort(List<string> args, int port)
    {
        port = NormalizeSshPort(port);
        if (port != 22) args.AddRange(["-p", port.ToString(CultureInfo.InvariantCulture)]);
    }
    private static string[] Finish(List<string> args, SshConfiguration cfg, string? keyPath) => keyPath is null
        ? [.. args, cfg.HostAlias]
        : ["-i", keyPath, "-o", "IdentitiesOnly=yes", .. args, $"{cfg.User}@{cfg.VmHost}"];

    [GeneratedRegex(@"^[0-9a-zA-Z\-.:]+$")]
    private static partial Regex Zone();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();
}
