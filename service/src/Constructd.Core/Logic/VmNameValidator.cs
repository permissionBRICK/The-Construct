using System.Text.RegularExpressions;

namespace Constructd.Core.Logic;

/// <summary>
/// THE ONE INSTANCE-NAME RULE. VM names double as SSH aliases, key file names and config-sync
/// branch suffixes (plan §4.3), so they are restricted to a lowercase DNS label. Mirrored verbatim
/// by <c>NAME_RE</c> in <c>extension/src/instances.js</c>, <c>$script:ConstructInstanceNameRe</c> in
/// <c>lib/AgentVm.Instances.ps1</c> and the <c>-VmName</c> check in <c>Auto-Install.ps1</c> /
/// <c>Create-AgentVM.ps1</c> — change all four together.
/// <list type="bullet">
/// <item>Alphanumeric FIRST <b>and LAST</b>: <c>work-</c> derives the endpoint
/// <c>work-.mshome.net</c>, which is not a host name at all, so a VM accepted under that name could
/// never be recorded in the client registry.</item>
/// <item>1-63 characters — the DNS label's own limit, because the name IS a label of
/// <c>&lt;name&gt;.mshome.net</c>. The client's key-file rule carries its own longer bound so the
/// derived <c>construct_&lt;63-char-name&gt;_ed25519</c> (81 characters) stays valid.</item>
/// <item><c>construct-</c> is a RESERVED prefix — it is the namespace the derived key file
/// (<c>construct_&lt;name&gt;_ed25519</c>) and branch (<c>vm-&lt;name&gt;</c>) live in, and it is the
/// exact name whose prefix the config-branch derivation used to strip, aliasing a different
/// instance's config store.</item>
/// <item><c>\A</c>/<c>\z</c>, not <c>^</c>/<c>$</c>: .NET's <c>$</c> also matches just before a
/// trailing newline, so <c>"work\n"</c> would be valid here and invalid in JavaScript — and the
/// service and the client must never disagree about which names exist.</item>
/// </list>
/// </summary>
public static partial class VmNameValidator
{
    public const string Pattern = @"\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z";

    /// <summary>The reserved name prefix (compared case-insensitively).</summary>
    public const string ReservedPrefix = "construct-";

    /// <summary>The one human-readable statement of the rule, shared with the extension
    /// (<c>instances.NAME_RULE</c>) and the PowerShell installers. ASCII only.</summary>
    public const string Rule = "1-63 lowercase letters, digits or hyphens, starting and ending with a letter or digit; names starting with \"construct-\" are reserved.";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && !IsReserved(name) && NameRegex().IsMatch(name);

    /// <summary>Does this name claim the reserved prefix? Case-insensitive, so a display-cased
    /// name asks the same question the PowerShell installers do.</summary>
    public static bool IsReserved(string? name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase);
}
