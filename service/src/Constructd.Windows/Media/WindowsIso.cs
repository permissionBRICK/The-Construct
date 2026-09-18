using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using DiscUtils.Udf;
using DiscUtils.Iso9660;
namespace Constructd.Windows.Media;

/// <summary>Reads UDF without mounting media and changes only the EFI boot catalog pointer.</summary>
public static class WindowsIso
{
    public static WindowsMedia Inspect(Stream iso)
    {
        using var udf = new UdfReader(iso);
        var path = new[] { @"sources\install.wim", @"sources\install.esd" }.FirstOrDefault(udf.FileExists) ?? throw new MediaException("windows-image-missing");
        using var wim = udf.OpenFile(path, FileMode.Open);
        var header = new byte[96]; wim.ReadExactly(header);
        if (!header.AsSpan(0, 8).SequenceEqual("MSWIM\0\0\0"u8)) throw new MediaException("windows-image-invalid");
        var length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72)) & 0x00ffffffffffffff;
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(80));
        if (length is 0 or > 4194304 || offset > (ulong)wim.Length || length > (ulong)wim.Length - offset) throw new MediaException("windows-image-invalid");
        wim.Position = (long)offset; var bytes = new byte[(int)length]; wim.ReadExactly(bytes);
        var xml = XDocument.Parse(Encoding.Unicode.GetString(bytes).Trim('\0', '\uFEFF'));
        return FromWimXml(xml);
    }
    public static WindowsMedia FromWimXml(XDocument xml)
    {
        var images = new List<WindowsImage>(); string language = "";
        foreach (var image in xml.Descendants("IMAGE"))
        {
            var windows = image.Element("WINDOWS"); var name = (string?)image.Element("NAME") ?? "";
            var build = (string?)windows?.Element("VERSION")?.Element("BUILD") ?? "";
            var edition = ((string?)windows?.Element("EDITIONID") ?? "").Replace("Eval", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
            var product = name.Contains("2025") ? "server2025" : name.Contains("2022") ? "server2022" : name.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) ? "win11" : "";
            edition = edition switch { "professional" => "pro", "professionaln" => "pro-n", "serverstandard" => "standard", "serverdatacenter" => "datacenter", _ => edition };
            if (product.StartsWith("server") && (string?)windows?.Element("INSTALLATIONTYPE") == "Server Core") edition += "-core";
            if (product.Length == 0) continue;
            try { _ = Core.Logic.WindowsUnattendRenderer.Parse(product + "-" + edition); } catch (Core.Logic.ChildValidationException) { continue; }
            language = (string?)windows?.Element("LANGUAGES")?.Element("DEFAULT") ?? language;
            images.Add(new(product, edition, name, (int)image.Attribute("INDEX")!, build,
                ((string?)windows?.Element("EDITIONID") ?? "").Contains("Eval", StringComparison.OrdinalIgnoreCase) || name.Contains("Evaluation", StringComparison.OrdinalIgnoreCase)));
        }
        if (images.Count == 0 || images.Select(i => i.Product).Distinct().Count() != 1) throw new MediaException("windows-image-unsupported");
        return new(images[0].Product, language, images);
    }

    public static async Task PrepareAsync(Stream source, Stream destination, CancellationToken ct)
    {
        byte[] boot;
        using (var udf = new UdfReader(source))
        using (var file = udf.OpenFile(@"efi\microsoft\boot\efisys_noprompt.bin", FileMode.Open))
        {
            if (file.Length is < 2048 or > 16777216 || file.Length % 2048 != 0) throw new MediaException("windows-boot-image-invalid");
            boot = new byte[(int)file.Length]; file.ReadExactly(boot);
        }
        var extent = await FindExtentAsync(source, boot, ct);
        source.Position = 0; await source.CopyToAsync(destination, ct);
        var patchOffset = EfiPointerOffset(source);
        destination.Position = patchOffset;
        var value = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(value, extent);
        await destination.WriteAsync(value, ct); await destination.FlushAsync(ct);
    }
    // A sector-aligned match verifies the entire no-prompt image, avoiding dependence on UDF internals.
    private static async Task<uint> FindExtentAsync(Stream iso, byte[] boot, CancellationToken ct)
    {
        iso.Position = 0; var block = new byte[2048]; uint sector = 0;
        while (iso.Position + block.Length <= iso.Length)
        {
            await iso.ReadExactlyAsync(block, ct);
            if (block.AsSpan().SequenceEqual(boot.AsSpan(0, 2048)))
            {
                var position = iso.Position; var rest = new byte[boot.Length - 2048];
                if (iso.Length - position >= rest.Length)
                {
                    await iso.ReadExactlyAsync(rest, ct);
                    if (rest.AsSpan().SequenceEqual(boot.AsSpan(2048))) return sector;
                }
                iso.Position = position;
            }
            sector++;
        }
        throw new MediaException("windows-boot-image-missing");
    }
    public static long EfiPointerOffset(Stream iso)
    {
        var descriptor = new byte[2048]; uint catalog = 0;
        for (var sector = 16; sector < 256; sector++)
        {
            iso.Position = sector * 2048L; iso.ReadExactly(descriptor);
            if (!descriptor.AsSpan(1, 5).SequenceEqual("CD001"u8)) continue;
            if (descriptor[0] == 0 && Encoding.ASCII.GetString(descriptor, 7, 23) == "EL TORITO SPECIFICATION") { catalog = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(71)); break; }
            if (descriptor[0] == 255) break;
        }
        if (catalog == 0 || catalog * 2048L + 2048 > iso.Length) throw new MediaException("windows-boot-catalog-invalid");
        iso.Position = catalog * 2048L; iso.ReadExactly(descriptor);
        var sum = 0; for (var i = 0; i < 32; i += 2) sum += BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(i));
        if (descriptor[0] != 1 || descriptor[30] != 0x55 || descriptor[31] != 0xaa || (sum & 0xffff) != 0) throw new MediaException("windows-boot-catalog-invalid");
        if (descriptor[1] == 0xef && descriptor[32] == 0x88) return catalog * 2048L + 40;
        for (var pos = 64; pos < 2016;)
        {
            var final = descriptor[pos] == 0x91;
            if (descriptor[pos] is not (0x90 or 0x91)) break;
            var platform = descriptor[pos + 1]; var count = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(pos + 2));
            if (count == 0 || pos + 32L + count * 32L > 2048) break;
            pos += 32;
            for (var j = 0; j < count; j++, pos += 32) if (platform == 0xef && descriptor[pos] == 0x88) return catalog * 2048L + pos + 8;
            if (final) break;
        }
        throw new MediaException("windows-efi-entry-missing");
    }
    public static async Task BuildAuxiliaryAsync(IReadOnlyDictionary<string, string> files, Stream destination, CancellationToken ct)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = "UNATTEND" };
        foreach (var file in files) builder.AddFile(file.Key, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(file.Value)).ToArray());
        using var iso = builder.Build(); await iso.CopyToAsync(destination, ct);
    }
}
