using System.Buffers.Binary;
using System.Xml.Linq;
using Constructd.Windows.Media;
using DiscUtils.Iso9660;
namespace Constructd.Tests.Windows;
public class WindowsIsoTests
{
    [Fact]
    public async Task Preparing_UDF_media_changes_only_the_four_byte_EFI_pointer()
    {
        // Synthetic UDF/Joliet fixture, no Microsoft content: 2 KiB 'B' BIOS image,
        // 4 KiB 'P' EFI image, 4 KiB 'N' no-prompt image, and a WIM XML header.
        // Built with genisoimage -udf -J -b bios.bin -no-emul-boot -eltorito-alt-boot
        // -e efi/microsoft/boot/efisys.bin -no-emul-boot -o fixture.iso tree/.
        var encoded = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Windows", "synthetic-windows.iso.gz.b64"));
        using var compressed = new MemoryStream(Convert.FromBase64String(encoded));
        using var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
        using var input = new MemoryStream(); await gzip.CopyToAsync(input);
        var original = input.ToArray(); input.Position = 0;
        var before = WindowsIso.Inspect(input);
        Assert.Equal("win11", before.Product); Assert.Equal("pro", Assert.Single(before.Images).Edition);
        using var output = new MemoryStream(); await WindowsIso.PrepareAsync(input, output, default);
        var patched = output.ToArray(); var offset = (int)WindowsIso.EfiPointerOffset(input);
        Assert.Equal(original.Length, patched.Length);
        Assert.Equal(original[..offset], patched[..offset]); Assert.Equal(original[(offset + 4)..], patched[(offset + 4)..]);
        Assert.NotEqual(original[offset..(offset + 4)], patched[offset..(offset + 4)]);
        var sector = BinaryPrimitives.ReadUInt32LittleEndian(patched.AsSpan(offset));
        Assert.All(patched[(int)(sector * 2048)..(int)(sector * 2048 + 4096)], b => Assert.Equal((byte)'N', b));
        output.Position = 0;
        Assert.Equal(before.Images, WindowsIso.Inspect(output).Images);
    }

    [Fact]
    public async Task Answer_iso_contains_Joliet_names_and_exact_content()
    {
        using var output = new MemoryStream();
        await WindowsIso.BuildAuxiliaryAsync(new Dictionary<string,string> { ["autounattend.xml"] = "<unattend />", ["firstlogon.ps1"] = "Write-Host ready" }, output, default);
        output.Position = 0; using var reader = new CDReader(output, true);
        using var file = reader.OpenFile("autounattend.xml", FileMode.Open); using var text = new StreamReader(file);
        Assert.Equal("<unattend />", text.ReadToEnd());
        Assert.True(reader.FileExists("firstlogon.ps1"));
    }
    [Fact]
    public void Reads_product_edition_core_and_evaluation_from_WIM_metadata()
    {
        var metadata = WindowsIso.FromWimXml(XDocument.Parse("""
            <WIM><IMAGE INDEX="2"><NAME>Windows Server 2025 SERVERDATACENTER</NAME><WINDOWS><EDITIONID>ServerDatacenterEval</EDITIONID><INSTALLATIONTYPE>Server Core</INSTALLATIONTYPE><VERSION><BUILD>26100</BUILD></VERSION><LANGUAGES><DEFAULT>en-US</DEFAULT></LANGUAGES></WINDOWS></IMAGE></WIM>
            """));
        var image = Assert.Single(metadata.Images);
        Assert.Equal("server2025", image.Product); Assert.Equal("datacenter-core", image.Edition); Assert.True(image.Evaluation);
    }
    [Fact]
    public void Locates_only_EFI_catalog_pointer_and_rejects_bad_checksum()
    {
        var data = new byte[40 * 2048]; var d = data.AsSpan(17 * 2048, 2048);
        "CD001"u8.CopyTo(d[1..]); "EL TORITO SPECIFICATION"u8.CopyTo(d[7..]); BinaryPrimitives.WriteUInt32LittleEndian(d[71..], 30);
        var c = data.AsSpan(30 * 2048, 2048); c[0] = 1; c[30] = 0x55; c[31] = 0xaa;
        BinaryPrimitives.WriteUInt16LittleEndian(c[28..], unchecked((ushort)-(1 + 0xaa55)));
        c[32] = 0x88; c[64] = 0x91; c[65] = 0xef; c[66] = 1; c[96] = 0x88;
        using var input = new MemoryStream(data);
        Assert.Equal(30 * 2048 + 104, WindowsIso.EfiPointerOffset(input));
        data[30 * 2048 + 28]++;
        Assert.Throws<Constructd.Core.Abstractions.MediaException>(() => WindowsIso.EfiPointerOffset(input));
    }
}
