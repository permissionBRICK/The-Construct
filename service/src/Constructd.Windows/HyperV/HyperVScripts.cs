using Constructd.Windows.Internal;

namespace Constructd.Windows.HyperV;

/// <summary>
/// Where the Hyper-V driver's own PowerShell lives: <c>hyperv\</c> next to the service executable,
/// copied there by the build from <c>HyperV/Scripts</c>. The driver dot-sources these files and needs
/// no Construct checkout. The publish directory is hardened like the executable it holds, so the files
/// are as trustworthy as the service itself.
/// </summary>
public static class HyperVScripts
{
    /// <summary>The primary-VM contract functions, and the removal they route to.</summary>
    public const string Driver = "HyperV.Driver.ps1";

    /// <summary>The child-VM, inventory and guest-address functions.</summary>
    public const string ChildVm = "HyperV.ChildVm.ps1";

    /// <summary>The Windows-guest licensing functions of the child driver.</summary>
    public const string WindowsLicense = "HyperV.WindowsLicense.ps1";

    public static readonly IReadOnlyList<string> All = [Driver, ChildVm, WindowsLicense];

    /// <summary>The directory the build copies the scripts to.</summary>
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "hyperv");

    /// <summary>The full path of one script.</summary>
    public static string PathOf(string file) => Path.Combine(Directory, file);

    /// <summary>A dot-source statement for one script, its path quoted as a PowerShell literal.</summary>
    internal static string DotSource(string file) => ". " + PowerShellLiteral.Quote(PathOf(file));
}
