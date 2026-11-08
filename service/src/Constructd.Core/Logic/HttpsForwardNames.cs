using System.Globalization;
using System.Text.RegularExpressions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// THE NAME AN HTTPS FORWARD IS SERVED UNDER on the host's shared HTTPS port (docs/https-forwards.md).
///
/// <para>Every HTTPS forward of a VM gets its own host name, <c>&lt;vm&gt;-&lt;name&gt;</c> rendered through the per-VM
/// name pattern (<c>{name}.vms.example</c> gives <c>work-vm-app.vms.example</c>). One label, so the wildcard
/// certificate and the wildcard DNS record that already cover the VM's own name cover it too. Browsers scope
/// cookies and sessions by host and ignore the port, so a name per forward keeps two web apps of one VM, and
/// the VM's main UI, apart; port-only addresses under the VM's name share everything.</para>
///
/// <para>The name defaults to the VM port, which stays the same when a forward is recreated. An explicit name
/// is one lowercase DNS label, and <c>&lt;vm&gt;-&lt;name&gt;</c> must fit in one label (63 characters).</para>
/// </summary>
public static partial class HttpsForwardNames
{
    /// <summary>The longest DNS label.</summary>
    public const int MaxLabel = 63;

    /// <summary>What a name may look like, for error messages.</summary>
    public const string Rule = "lowercase letters, digits and hyphens, starting and ending with a letter or digit";

    [GeneratedRegex(@"\A[a-z0-9]([a-z0-9-]*[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    /// <summary>The forward's name: the stored one, or the VM port.</summary>
    public static string Effective(PortForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        return string.IsNullOrWhiteSpace(forward.HttpsName)
            ? forward.VmPort.ToString(CultureInfo.InvariantCulture)
            : forward.HttpsName;
    }

    /// <summary>The single DNS label a forward is served under: <c>&lt;vm&gt;-&lt;name&gt;</c>, lowercase.</summary>
    public static string Label(string vmName, string name) =>
        string.Concat(vmName.Trim(), "-", name.Trim()).ToLowerInvariant();

    /// <summary>
    /// Why <paramref name="name"/> cannot name an HTTPS forward of <paramref name="vmName"/>, or null when it can.
    /// The name is compared as given: callers lowercase it first if they accept upper case.
    /// </summary>
    public static string? Problem(string vmName, string name)
    {
        if (string.IsNullOrEmpty(name) || !LabelRegex().IsMatch(name))
            return $"'{name}' is not a forward name: use {Rule}.";
        var label = Label(vmName, name);
        if (label.Length > MaxLabel)
            return $"'{label}' is {label.Length} characters; a host name label holds at most {MaxLabel}. Use a shorter name.";
        return null;
    }

    /// <summary>
    /// The host a forward of <paramref name="vmName"/> named <paramref name="name"/> is served under, or null
    /// when there is none: no per-VM name pattern, or a label that is not a valid one.
    /// </summary>
    public static string? Host(string? pattern, string publicHost, string vmName, string name)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var label = Label(vmName, name);
        if (label.Length > MaxLabel || !LabelRegex().IsMatch(label)) return null;
        // Resolve falls back to the service's own PublicHost for anything that does not render to a host name;
        // that fallback must never become a forward's address.
        var host = PublicHostPatternRules.Resolve(pattern, publicHost, label);
        return string.Equals(host, publicHost, StringComparison.OrdinalIgnoreCase) ? null : host;
    }
}
