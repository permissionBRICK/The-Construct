using System.Text.RegularExpressions;

namespace Constructd.Core.Logic;

/// <summary>
/// THE PER-VM PUBLIC HOST NAME RULE (plan §4.12, "Public hostname per VM").
///
/// Two T3 web UIs on one remote host must not share a host name and differ only by port — browsers
/// scope cookies by host, not by port, so the second UI logs the first one out. The fix is one DNS
/// name per VM: the admin points a wildcard record (<c>*.vpn.example</c>) at the service host and
/// configures <c>Constructd:PublicHostPattern</c> = <c>{name}.vpn.example</c>. Every VM then has its
/// own host name, and every forward of that VM is advertised under it.
///
/// <para>The pattern is validated AT STARTUP, not per request: a pattern that renders to something
/// that is not a host name would otherwise surface as a broken URL handed to a user weeks later. It
/// must contain <c>{name}</c> (ordinal, exactly once) and render to a valid DNS name for EVERY valid
/// instance name — which is checked by rendering the extreme ones (see <see cref="Probes"/>) rather
/// than by reasoning about the pattern's shape.</para>
///
/// <para>Unset (the default) means "fall back to <see cref="Configuration.ConstructdOptions.PublicHost"/>",
/// which is byte-for-byte today's behaviour: every VM is advertised on the one LAN name.</para>
/// </summary>
public static partial class PublicHostPatternRules
{
    /// <summary>The one placeholder, matched ORDINALLY: <c>{NAME}</c> is not it.</summary>
    public const string Placeholder = "{name}";

    /// <summary>
    /// A DNS host name / FQDN. Mirrors <c>HOSTNAME_RE</c> in <c>extension/src/instances.js</c> and
    /// <c>$script:ConstructHostNameRe</c> in <c>lib/AgentVm.Instances.ps1</c>, because the clients
    /// validate exactly the value this renders before they store it in the registry: a pattern this
    /// accepts and they refuse would produce VMs that cannot be recorded.
    /// <c>\A</c>/<c>\z</c>, not <c>^</c>/<c>$</c> — .NET's <c>$</c> also matches before a trailing
    /// newline, and JavaScript's does not.
    /// </summary>
    public const string HostNamePattern =
        @"\A(?=.{1,253}\z)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z";

    /// <summary>
    /// The instance names a pattern is proved against: the shortest one there is, a digits-only one
    /// (a DNS label may start with a digit and our name rule allows it), and the longest one
    /// <see cref="VmNameValidator"/> accepts. The last is what catches a pattern whose fixed part
    /// pushes the rendered label past 63 characters or the whole name past 253.
    /// </summary>
    private static readonly string[] Probes =
    [
        "a",
        "0",
        "a" + new string('b', 61) + "c",
    ];

    [GeneratedRegex(HostNamePattern, RegexOptions.CultureInvariant)]
    private static partial Regex HostNameRegex();

    /// <summary>Is this a DNS host name (not an IP literal, not an empty string)?</summary>
    public static bool IsHostName(string? value) =>
        !string.IsNullOrEmpty(value) && HostNameRegex().IsMatch(value);

    /// <summary>
    /// Why this pattern is unusable, or <c>null</c> when it is fine. An empty/whitespace pattern is
    /// FINE: it means "unset", i.e. fall back to <c>PublicHost</c>.
    /// </summary>
    public static string? Validate(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        var value = pattern.Trim();
        var occurrences = Occurrences(value, Placeholder);

        if (occurrences == 0)
        {
            return $"Constructd:PublicHostPattern '{value}' does not contain '{Placeholder}', so every VM " +
                   "would be advertised on the same host name. Write it as '{name}.vpn.example', or leave " +
                   "the setting unset to use Constructd:PublicHost for every VM.";
        }

        if (occurrences > 1)
        {
            return $"Constructd:PublicHostPattern '{value}' contains '{Placeholder}' {occurrences} times; " +
                   "it must appear exactly once.";
        }

        foreach (var probe in Probes)
        {
            var rendered = Render(value, probe);
            if (!IsHostName(rendered))
            {
                return $"Constructd:PublicHostPattern '{value}' does not render to a valid DNS name for " +
                       $"every VM name: the VM '{probe}' would be advertised as '{rendered}'.";
            }
        }

        return null;
    }

    /// <summary>
    /// The public host name of one VM: the rendered pattern, or <paramref name="publicHost"/> when no
    /// pattern is configured. A rendering that is somehow not a host name falls back too — startup
    /// validation makes that unreachable for a valid VM name, and advertising the service's own LAN
    /// name is the answer that at least resolves.
    /// </summary>
    public static string Resolve(string? pattern, string publicHost, string? vmName)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(vmName))
        {
            return publicHost;
        }

        var rendered = Render(pattern.Trim(), vmName.Trim());
        return IsHostName(rendered) ? rendered : publicHost;
    }

    private static string Render(string pattern, string name) =>
        pattern.Replace(Placeholder, name, StringComparison.Ordinal);

    private static int Occurrences(string value, string needle)
    {
        var count = 0;
        var at = value.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = value.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
