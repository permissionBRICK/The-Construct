using System.Net;
using System.Net.Sockets;

namespace Constructd.Windows.Forwards;

/// <summary>One <c>v4tov4</c> rule as netsh reports it.</summary>
public sealed record PortProxyRule(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort);

/// <summary>
/// Parses <c>netsh interface portproxy show v4tov4</c>.
///
/// netsh is localized — the header of a German host reads "Lauschen auf ipv4:" / "Adresse Port" — and
/// the column widths shift with the data, so the parser ignores headers, separators and blank lines
/// entirely and looks only for the shape of a rule: four whitespace-separated tokens that are
/// address, port, address, port. That is language-independent, which is exactly the point: startup
/// reconciliation must not quietly do nothing on a host that was installed in another language.
/// </summary>
public static class PortProxyParser
{
    public static IReadOnlyList<PortProxyRule> Parse(string? output)
    {
        var rules = new List<PortProxyRule>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return rules;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var tokens = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length != 4)
            {
                continue;
            }

            if (!TryAddress(tokens[0], out var listenAddress) ||
                !TryPort(tokens[1], out var listenPort) ||
                !TryAddress(tokens[2], out var connectAddress) ||
                !TryPort(tokens[3], out var connectPort))
            {
                continue;
            }

            rules.Add(new PortProxyRule(listenAddress, listenPort, connectAddress, connectPort));
        }

        return rules;
    }

    /// <summary>An IPv4 literal, or the wildcard netsh prints for "any address" on some builds.</summary>
    private static bool TryAddress(string token, out string address)
    {
        if (token == "*")
        {
            address = "0.0.0.0";
            return true;
        }

        if (IPAddress.TryParse(token, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            address = parsed.ToString();
            return true;
        }

        address = string.Empty;
        return false;
    }

    private static bool TryPort(string token, out int port) =>
        int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535;
}
