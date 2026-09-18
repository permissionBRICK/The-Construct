using System.Buffers.Binary;
using System.Xml.Linq;
using Constructd.Windows.Media;
using DiscUtils.Iso9660;
namespace Constructd.Tests.Windows;
public class WindowsIsoTests
{
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
