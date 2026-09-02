using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Constructd.Core.Logic;

namespace Constructd.Windows.Internal;

/// <summary>
/// Raised when a value would reach <c>powershell.exe</c>, <c>wsl.exe</c> or <c>netsh.exe</c> and is
/// not one this service is willing to pass on. The message names the setting, never the value: the
/// value can be a path or a credential.
/// </summary>
public sealed class InvalidPlatformArgumentException(string parameterName, string reason)
    : ArgumentException($"Refusing to run: {parameterName} {reason}.", parameterName), IConstructdError
{
}

/// <summary>
/// Validates everything the Windows implementations hand to a child process, before it gets there.
///
/// The argument vector already prevents one value from becoming two arguments
/// (<see cref="Core.Abstractions.IProcessRunner"/>). This is the second line: the Hyper-V driver
/// composes a PowerShell <em>script</em>, so a value that carried a newline or an unbalanced quote
/// could add a statement to it. Every value is therefore checked here and single-quoted with
/// <see cref="PowerShellLiteral"/>; nothing is interpolated raw.
/// </summary>
internal static class ArgumentGuard
{
    /// <summary>A VM name: the same lowercase DNS label the API accepts, nothing else.</summary>
    public static string VmName(string? value, string parameterName = "vm name")
    {
        if (!VmNameValidator.IsValid(value))
        {
            throw new InvalidPlatformArgumentException(parameterName, $"must match {VmNameValidator.Pattern}");
        }

        return value!;
    }

    /// <summary>Free text (a switch name, a distro, a seed user): no control characters, bounded.</summary>
    public static string Text(string? value, string parameterName, int maxLength = 256)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidPlatformArgumentException(parameterName, "must not be empty");
        }

        if (value.Length > maxLength)
        {
            throw new InvalidPlatformArgumentException(parameterName, $"must be at most {maxLength} characters");
        }

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new InvalidPlatformArgumentException(parameterName, "must not contain control characters");
            }
        }

        return value;
    }

    /// <summary>
    /// An absolute Windows path on a drive letter. UNC and relative paths are refused: the WSL path
    /// mapping only has a deterministic answer for drive letters (<c>C:\x</c> → <c>/mnt/c/x</c>), and a
    /// relative path would resolve against the service's working directory, which is
    /// <c>%SystemRoot%\System32</c> under the SCM.
    /// </summary>
    public static string WindowsPath(string? value, string parameterName)
    {
        var path = Text(value, parameterName, maxLength: 4096);

        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/'))
        {
            throw new InvalidPlatformArgumentException(
                parameterName, "must be an absolute path on a drive letter (C:\\…)");
        }

        if (path.Contains('"', StringComparison.Ordinal))
        {
            throw new InvalidPlatformArgumentException(parameterName, "must not contain a quote character");
        }

        return path;
    }

    /// <summary>A TCP port number.</summary>
    public static int Port(int value, string parameterName)
    {
        if (value is < 1 or > 65535)
        {
            throw new InvalidPlatformArgumentException(parameterName, "must be a port between 1 and 65535");
        }

        return value;
    }

    /// <summary>A positive count (vCPUs, GB).</summary>
    public static int Positive(int value, string parameterName, int max)
    {
        if (value < 1 || value > max)
        {
            throw new InvalidPlatformArgumentException(parameterName, $"must be between 1 and {max}");
        }

        return value;
    }

    /// <summary>An IPv4 literal — what a netsh portproxy rule may carry as an address.</summary>
    public static string IPv4(string? value, string parameterName)
    {
        var text = Text(value, parameterName, maxLength: 45);

        if (!IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidPlatformArgumentException(parameterName, "must be an IPv4 address");
        }

        return address.ToString();
    }

    /// <summary>
    /// A value passed as <c>NAME=value</c> to <c>env</c> inside WSL. It is one argv element, so the
    /// only real hazards are a newline (which would end the assignment for anything that later parses
    /// the line) and a NUL; both are control characters.
    /// </summary>
    public static string EnvironmentValue(string? value, string parameterName, bool allowEmpty = false)
    {
        if (allowEmpty && string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Text(value, parameterName, maxLength: 4096);
    }

    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Renders a validated value as a PowerShell literal.</summary>
internal static class PowerShellLiteral
{
    /// <summary>
    /// A single-quoted PowerShell string. Single-quoted strings expand nothing — no <c>$</c>, no
    /// backtick, no subexpression — so doubling the embedded quote is the complete escape. The value
    /// must already have been through <see cref="ArgumentGuard"/>, which is what rules out a newline
    /// closing the statement.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                throw new InvalidPlatformArgumentException("script argument", "must not contain control characters");
            }
        }

        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    /// <summary>A PowerShell boolean literal.</summary>
    public static string Bool(bool value) => value ? "$true" : "$false";
}
