using System.Xml.Linq;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Tests.Windows;

public class WindowsUnattendTests
{
    [Theory]
    [InlineData("win11-pro")]
    [InlineData("server2022-standard")]
    [InlineData("server2025-datacenter-core")]
    public void Renders_selected_image_and_escaped_credentials_with_GPT(string selector)
    {
        var s = WindowsUnattendRenderer.Parse(selector);
        var files = WindowsUnattendRenderer.Render(s, new(s.Product, s.Edition, "exact WIM name", 4, "26100"), new("a<&\"secret", "LAB-VM"));
        var xml = XDocument.Parse(files["autounattend.xml"]); XNamespace n = "urn:schemas-microsoft-com:unattend";
        Assert.Equal("4", xml.Descendants(n + "MetaData").Single().Element(n + "Value")!.Value);
        Assert.Equal("a<&\"secret", xml.Descendants(n + "AdministratorPassword").Single().Element(n + "Value")!.Value);
        Assert.Contains(xml.Descendants(n + "Type"), e => e.Value == "EFI");
        Assert.DoesNotContain(xml.Descendants(n + "component"), e => ((string?)e.Attribute("name"))!.Contains("TerminalServices"));
        Assert.Contains("sshd", files["firstlogon.ps1"]);
        Assert.Equal(selector.StartsWith("win11"), files["firstlogon.ps1"].Contains("Get-AppxPackage"));
    }
    [Theory]
    [InlineData("win11-pro")]
    [InlineData("server2022-standard")]
    [InlineData("server2025-datacenter-core")]
    public void Reuse_preview_requests_skip_auto_activation_in_specialize(string selector)
    {
        var selection = WindowsUnattendRenderer.Parse(selector);
        var files = WindowsUnattendRenderer.Render(selection, new(selection.Product, selection.Edition, "image", 1, "26100"), new("secret"), true);
        var xml = XDocument.Parse(files["autounattend.xml"]); XNamespace n = "urn:schemas-microsoft-com:unattend";
        var skip = Assert.Single(xml.Descendants(n + "SkipAutoActivation"));
        Assert.Equal("true", skip.Value);
        Assert.Equal("specialize", (string?)skip.Parent!.Parent!.Attribute("pass"));
        Assert.False(new Constructd.Core.Configuration.ConstructdOptions().WindowsLicenseReuse);
    }
    [Fact]
    public void Refuses_cross_product_editions_and_path_traversal()
    {
        Assert.Throws<ChildValidationException>(() => WindowsUnattendRenderer.Parse("server2022-pro"));
        Assert.Throws<ChildValidationException>(() => WindowsUnattendRenderer.Validate(new("secret", Files: new Dictionary<string,string> { ["../escape"] = "x" })));
        Assert.Throws<ChildValidationException>(() => WindowsUnattendRenderer.Render(new("win11", "pro"), new("server2022", "standard", "server", 1, "20348"), new("secret")));
    }
    [Fact]
    public void Evaluation_server_has_no_incompatible_setup_key()
    {
        var xml = WindowsUnattendRenderer.Render(new("server2022", "standard"), new("server2022", "standard", "eval", 2, "20348", true), new("secret"))["autounattend.xml"];
        Assert.DoesNotContain("ProductKey", xml);
    }
}
