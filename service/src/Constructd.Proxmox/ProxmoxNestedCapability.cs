namespace Constructd.Proxmox;

public static class ProxmoxNestedCapability
{
    public static bool IsAvailable(string modulesPath = "/sys/module")
    {
        foreach (var module in new[] { "kvm_intel", "kvm_amd" })
        {
            try
            {
                var value = File.ReadAllText(Path.Combine(modulesPath, module, "parameters", "nested")).Trim();
                if (value is "Y" or "y" or "1") return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}
