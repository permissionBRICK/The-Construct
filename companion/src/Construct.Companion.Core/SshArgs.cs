using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Construct.Companion.Core;

// Ipv4 adds `-o AddressFamily=inet`: a host service's SSH forwards listen on IPv4 only, while
// its advertised name can resolve to IPv6 first and Windows OpenSSH does not fall back.
public sealed record SshConfiguration(string VmHost = "agent-vm.mshome.net", string HostAlias = "agent-vm",
    string User = "root", string KeyName = "agent_vm_ed25519", int SshPort = 22, int ConnectTimeout = 12, bool Ipv4 = false);

public static partial class SshArgs
{
    // The connection for a normalized registry instance (instances.js toSshCfg): a VM on a
    // host service is dialled over IPv4 unless its endpoint is an IPv6 literal.
    public static SshConfiguration ForInstance(System.Text.Json.Nodes.JsonObject instance, int connectTimeout)
    {
        var vmHost = State.StateJson.String(instance["vmHost"]);
        return new SshConfiguration(vmHost, State.StateJson.String(instance["hostAlias"]),
            KeyName: State.StateJson.String(instance["keyName"]), SshPort: State.Instances.CoercePort(instance["sshPort"]) ?? 22,
            ConnectTimeout: connectTimeout,
            Ipv4: State.Instances.IsRemoteBackend(State.StateJson.Text(instance["backend"])) && !State.Instances.IsIpv6Literal(vmHost));
    }
    public static string[] Build(SshConfiguration cfg, string command, string? keyPath = null)
    {
        var args = Common(cfg);
        AddPort(args, cfg.SshPort);
        return Finish(args, cfg, keyPath).Append(command).ToArray();
    }

    // Long-lived `ssh -T` running a script from stdin-free base64; keepalives detect a dead VM.
    public static string[] BuildWatch(SshConfiguration cfg, string script, string? keyPath = null)
    {
        var args = new List<string> { "-T" };
        args.AddRange(Common(cfg));
        args.AddRange(["-o", "ServerAliveInterval=20", "-o", "ServerAliveCountMax=3"]);
        AddPort(args, cfg.SshPort);
        return Finish(args, cfg, keyPath).Append(WrapScriptCommand(script)).ToArray();
    }

    public static string[] BuildLocalForward(SshConfiguration cfg, int localPort, int vmPort,
        string? keyPath = null, string? bindHost = null, string? connectAddress = null, int? connectPort = null)
    {
        if (!ValidPort(localPort) || !ValidPort(vmPort) || !ValidPort(connectPort ?? vmPort))
            throw new ArgumentException("Invalid forward port.");
        var far = NormalizeConnectAddress(connectAddress) ?? throw new ArgumentException("Invalid forward destination.");
        return Forward(cfg, keyPath, "-L", $"{NormalizeBindHost(bindHost)}:{localPort}:{far}:{connectPort ?? vmPort}");
    }

    // Microphone passthrough: the VM's recorder port reaches the loopback capture server on this PC.
    public static string[] BuildReverseForward(SshConfiguration cfg, int vmPort, int hostPort, string? keyPath = null) =>
        Forward(cfg, keyPath, "-R", $"{Audio.AudioProtocol.NormalizePort(vmPort, 8767)}:127.0.0.1:{Audio.AudioProtocol.NormalizePort(hostPort, 0)}");

    private static string[] Forward(SshConfiguration cfg, string? keyPath, string flag, string spec)
    {
        var args = new List<string> { "-N" };
        args.AddRange(Common(cfg));
        args.AddRange(["-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ExitOnForwardFailure=yes", flag, spec]);
        AddPort(args, cfg.SshPort);
        return Finish(args, cfg, keyPath);
    }

    public static string WrapScriptCommand(string script)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        return $"f=$(mktemp) && printf %s '{b64}' | base64 -d > \"$f\" && bash \"$f\"; rc=$?; rm -f \"$f\"; exit $rc";
    }
    public static int NormalizeSshPort(int port) => ValidPort(port) ? port : 22;
    public static string NormalizeBindHost(string? host) => State.StateJson.Trim(host ?? "") is "0.0.0.0" or "*" or "::" ? "0.0.0.0" : "127.0.0.1";
    public static string? NormalizeConnectAddress(string? value)
    {
        var text = State.StateJson.Trim(value ?? "");
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
        "-o", "ConnectTimeout=" + cfg.ConnectTimeout.ToString(CultureInfo.InvariantCulture), .. (cfg.Ipv4 ? ["-o", "AddressFamily=inet"] : Array.Empty<string>())];
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
