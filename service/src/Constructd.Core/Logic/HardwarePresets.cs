using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public sealed class ChildValidationException(string code, string field) : Exception($"Child VM request refused: {code} ({field})."), IConstructdError
{
    public string Code { get; } = code;
    public string Field { get; } = field;
}

public static class HardwarePresets
{
    public static ChildHardware Resolve(int cpus, int ramMb, int diskGb, string? preset, int? generation,
        bool? secureBoot, SecureBootTemplate? template, bool? tpm, IReadOnlyList<BootDevice>? order, bool auxiliary, bool network)
    {
        if (preset is not (null or "windows" or "linux")) throw new ChildValidationException("validation", "preset");
        var secure = secureBoot ?? preset is not null;
        var effectiveTemplate = template ?? preset switch { "windows" => SecureBootTemplate.MicrosoftWindows, "linux" => SecureBootTemplate.MicrosoftUefiCertificateAuthority, _ => (SecureBootTemplate?)null };
        var h = new ChildHardware(cpus, ramMb, diskGb, generation ?? 2, secure, effectiveTemplate, tpm ?? preset == "windows", BootOrderRules.Resolve(order, auxiliary, network), network);
        ValidateShape(h);
        return h;
    }

    public static void ValidateShape(ChildHardware h)
    {
        if (h.Cpus < 1 || h.Cpus > 512) throw new ChildValidationException("validation", "cpus");
        if (h.RamMb < 512 || h.RamMb % 2 != 0) throw new ChildValidationException("validation", "ramMb");
        if (h.DiskGb < 1 || h.DiskGb > 65536) throw new ChildValidationException("validation", "diskGb");
        if (h.SecureBoot && h.SecureBootTemplate is null) throw new ChildValidationException("validation", "secureBootTemplate");
        if (h.BootOrder is null || h.BootOrder.Any(x => !Enum.IsDefined(x)) || h.BootOrder.Distinct().Count() != h.BootOrder.Count)
            throw new ChildValidationException("validation", "bootOrder");
    }

    public static void ValidateCapabilities(ChildHardware h, BackendCapabilities caps, bool auxiliary)
    {
        ValidateShape(h);
        void Require(bool condition, string field) { if (!condition) throw new ChildValidationException("unsupported-capability", field); }
        Require(caps.Generations.Contains(h.Generation), "generation");
        Require(h.DynamicMemory is null, "dynamicMemory");
        Require(!h.SecureBoot || caps.SecureBoot != CapabilityLevel.Unsupported, "secureBoot");
        Require(h.SecureBootTemplate is null || caps.SecureBootTemplates.Contains(h.SecureBootTemplate.Value), "secureBootTemplate");
        Require(!h.Tpm || caps.Tpm != CapabilityLevel.Unsupported, "tpm");
        Require(caps.MaxOpticalDrives >= (auxiliary ? 2 : 1) && (!auxiliary || caps.AuxiliaryMedia != CapabilityLevel.Unsupported), "auxiliaryMedia");
        Require(h.BootOrder.Count == 0 || caps.BootOrder != CapabilityLevel.Unsupported, "bootOrder");
    }
}
