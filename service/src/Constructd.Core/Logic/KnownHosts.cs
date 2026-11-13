using System.Security.Cryptography;
using System.Text;

namespace Constructd.Core.Logic;

/// <summary>
/// Edits the engine's own <c>known_hosts</c> file (the one <see cref="Abstractions.SshTarget.KnownHostsFile"/>
/// names). Only a reinstall calls it: the rebuilt VM has a new host key, which
/// <c>StrictHostKeyChecking=accept-new</c> would refuse. Pure.
/// </summary>
public static class KnownHosts
{
    /// <summary>The name ssh stores for an address: <c>host</c> on port 22, <c>[host]:port</c> otherwise.</summary>
    public static string HostName(string host, int port) => port == 22 ? host : $"[{host}]:{port}";

    /// <summary>
    /// <paramref name="text"/> without the lines for <paramref name="host"/>:<paramref name="port"/>,
    /// plain (a comma-separated list containing the name) or hashed (<c>|1|salt|hash</c>, ssh's
    /// <c>HashKnownHosts</c>). A plain line that lists other names too loses only this name.
    /// </summary>
    public static string Remove(string text, string host, int port)
    {
        var name = HostName(host, port);
        var output = new StringBuilder();
        var lines = text.Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        foreach (var raw in lines.Take(count))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOfAny([' ', '\t']);
            if (trimmed.Length == 0 || trimmed[0] == '#' || space < 0)
            {
                Append(output, line);
                continue;
            }

            var marker = trimmed[0] == '@' ? trimmed[..space] + " " : "";
            var rest = marker.Length == 0 ? trimmed : trimmed[space..].TrimStart();
            var hostsEnd = rest.IndexOfAny([' ', '\t']);
            if (hostsEnd < 0) { Append(output, line); continue; }
            var hosts = rest[..hostsEnd];
            var tail = rest[hostsEnd..];

            if (hosts.StartsWith("|1|", StringComparison.Ordinal))
            {
                if (!HashedMatches(hosts, name)) Append(output, line);
                continue;
            }

            var names = hosts.Split(',');
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) { Append(output, line); continue; }
            var left = names.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (left.Length > 0) Append(output, marker + string.Join(',', left) + tail);
        }

        return output.ToString();
    }

    private static void Append(StringBuilder output, string line) => output.Append(line).Append('\n');

    private static bool HashedMatches(string hashed, string name)
    {
        var parts = hashed.Split('|');
        if (parts.Length != 4) return false;
        try
        {
            using var hmac = new HMACSHA1(Convert.FromBase64String(parts[2]));
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(hmac.ComputeHash(Encoding.ASCII.GetBytes(name.ToLowerInvariant())), expected);
        }
        catch (FormatException) { return false; }
    }
}
