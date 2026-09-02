using Constructd.Windows.Internal;

namespace Constructd.Windows.Iso;

/// <summary>
/// Windows path → WSL path, the same mapping <c>Auto-Install.ps1</c>'s <c>ConvertTo-WslPath</c> does
/// (<c>C:\Users\me\x.iso</c> → <c>/mnt/c/Users/me/x.iso</c>).
///
/// Mapped here rather than by calling <c>wslpath</c>, for the reason recorded in
/// <c>Auto-Install.ps1</c>: a path with backslashes passed through to <c>wsl.exe</c> loses them, so
/// <c>wslpath</c> would be handed <c>C:Usersmex.iso</c>. The default automount layout is
/// deterministic, so the mapping is done in-process.
/// </summary>
internal static class WslPath
{
    public static string FromWindows(string windowsPath, string parameterName)
    {
        var path = ArgumentGuard.WindowsPath(windowsPath, parameterName);

        var rest = path[3..].Replace('\\', '/');

        // A traversal segment would resolve inside WSL against a path we did not intend; the service
        // composes these paths itself, so refusing is free.
        foreach (var segment in rest.Split('/'))
        {
            if (segment == "..")
            {
                throw new InvalidPlatformArgumentException(parameterName, "must not contain '..' segments");
            }
        }

        return $"/mnt/{char.ToLowerInvariant(path[0])}/{rest}";
    }
}
