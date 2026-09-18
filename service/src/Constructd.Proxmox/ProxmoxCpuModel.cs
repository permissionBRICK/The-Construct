namespace Constructd.Proxmox;

/// <summary>
/// The CPU model handed to <c>qm</c>. Proxmox starts named models with <c>enforce</c>, so a model
/// that asks for a flag the host lacks makes QEMU exit instead of masking it. The default
/// non-nesting model <c>x86-64-v2-AES</c> requires AES-NI, which some older hosts (Nehalem-era
/// laptops) do not have; on such a host the <c>-AES</c> suffix is dropped so the VM starts.
/// <c>host</c> and any model without an <c>-AES</c> suffix pass through unchanged.
/// </summary>
public static class ProxmoxCpuModel
{
    public static string Resolve(string configured, string cpuInfoPath = "/proc/cpuinfo")
    {
        if (!configured.EndsWith("-AES", StringComparison.Ordinal) || HostHasAes(cpuInfoPath)) return configured;
        return configured[..^"-AES".Length];
    }

    public static bool HostHasAes(string cpuInfoPath = "/proc/cpuinfo")
    {
        try
        {
            foreach (var line in File.ReadLines(cpuInfoPath))
            {
                if (!line.StartsWith("flags", StringComparison.Ordinal)) continue;
                var colon = line.IndexOf(':');
                return colon >= 0 && line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("aes", StringComparer.Ordinal);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        // Unknown host: keep the configured model rather than silently weakening it.
        return true;
    }
}
