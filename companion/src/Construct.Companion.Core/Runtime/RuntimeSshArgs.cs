namespace Construct.Companion.Core.Runtime;

public static class RuntimeSshArgs
{
    public static string[] Watch(SshConfiguration cfg, string script, string? keyPath = null) => Build(cfg, keyPath,
        ["-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", $"ConnectTimeout={cfg.ConnectTimeout}",
        "-o", "ServerAliveInterval=20", "-o", "ServerAliveCountMax=3"], SshArgs.WrapScriptCommand(script));
    public static string[] Reverse(SshConfiguration cfg, int vmPort, int hostPort, string? keyPath = null) => Build(cfg, keyPath,
        ["-N", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", $"ConnectTimeout={cfg.ConnectTimeout}",
        "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3", "-o", "ExitOnForwardFailure=yes",
        "-R", $"{Audio.AudioProtocol.NormalizePort(vmPort, 8767)}:127.0.0.1:{Audio.AudioProtocol.NormalizePort(hostPort, 0)}"]);
    private static string[] Build(SshConfiguration cfg, string? keyPath, List<string> args, string? command = null)
    {
        var port = SshArgs.NormalizeSshPort(cfg.SshPort); if (port != 22) args.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.Add(keyPath is null ? cfg.HostAlias : $"{cfg.User}@{cfg.VmHost}");
        if (command is not null) args.Add(command);
        return keyPath is null ? args.ToArray() : ["-i", keyPath, "-o", "IdentitiesOnly=yes", .. args];
    }
}
