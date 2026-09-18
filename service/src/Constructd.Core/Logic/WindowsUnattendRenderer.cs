using System.Text.RegularExpressions;
using System.Xml.Linq;
using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class WindowsUnattendRenderer
{
    public static WindowsSelection Parse(string selector)
    {
        var split = selector.IndexOf('-');
        if (split < 0) throw Invalid("windows");
        var p = selector[..split]; var e = selector[(split + 1)..];
        if (!(p == "win11" && e is "pro" or "pro-n" or "enterprise" or "education") &&
            !(p is "server2022" or "server2025" && e is "standard" or "datacenter" or "standard-core" or "datacenter-core")) throw Invalid("windows");
        return new(p, e);
    }

    public static void Validate(WindowsUnattend u)
    {
        if (string.IsNullOrEmpty(u.AdminPassword) || u.AdminPassword.Length > 256 || u.AdminPassword.Any(char.IsControl)) throw Invalid("unattend.adminPassword");
        if (!Regex.IsMatch(u.Hostname, @"\A[a-zA-Z][a-zA-Z0-9-]{0,14}\z")) throw Invalid("unattend.hostname");
        if (!Regex.IsMatch(u.Locale, @"\A[a-z]{2,3}-[A-Z]{2}\z") || string.IsNullOrWhiteSpace(u.TimeZone) || u.TimeZone.Length > 128 || u.TimeZone.Any(char.IsControl)) throw Invalid("unattend.locale");
        if ((u.FirstLogonScript?.Length ?? 0) > 262144 || (u.Files?.Count ?? 0) > 64 || (u.Files?.Sum(f => f.Value.Length) ?? 0) > 1048576) throw Invalid("unattend.files");
        foreach (var f in u.Files ?? new Dictionary<string, string>())
            if (!Regex.IsMatch(f.Key, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\z") || f.Key.EndsWith('.') || f.Key.Contains("..") ||
                new[] { "autounattend.xml", "firstlogon.ps1", "extra.ps1", "construct-report.ps1" }.Contains(f.Key, StringComparer.OrdinalIgnoreCase)) throw Invalid("unattend.files");
    }

    public static IReadOnlyDictionary<string, string> Render(WindowsSelection selection, WindowsImage image, WindowsUnattend u)
    {
        Validate(u);
        if (image.Product != selection.Product || image.Edition != selection.Edition) throw Invalid("windows");
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        var family = selection.Product == "win11" ? "WindowsClient" : "WindowsServer";
        var doc = XDocument.Parse(Resource(family + ".autounattend.xml"));
        foreach (var comment in doc.DescendantNodes().OfType<XComment>().ToArray()) comment.Remove();
        // Both backends use UEFI. Retain the server template but replace its BIOS disk layout.
        var client = XDocument.Parse(Resource("WindowsClient.autounattend.xml"));
        doc.Descendants(ns + "DiskConfiguration").Single().ReplaceWith(new XElement(client.Descendants(ns + "DiskConfiguration").Single()));
        doc.Descendants(ns + "InstallTo").Single().Element(ns + "PartitionID")!.Value = "3";
        foreach (var component in doc.Descendants(ns + "component").Where(c => ((string?)c.Attribute("name"))?.Contains("TerminalServices") == true || (string?)c.Attribute("name") == "Networking-MPSSVC-Svc").ToArray()) component.Remove();
        foreach (var name in new[] { "InputLocale", "SystemLocale", "UILanguage", "UserLocale" }) foreach (var e in doc.Descendants(ns + name)) e.Value = u.Locale;
        doc.Descendants(ns + "ComputerName").Single().Value = u.Hostname;
        doc.Descendants(ns + "TimeZone").Single().Value = u.TimeZone;
        foreach (var e in doc.Descendants(ns + "Value").Where(e => e.Parent?.Name.LocalName is "AdministratorPassword" or "Password")) e.Value = u.AdminPassword;
        var metadata = doc.Descendants(ns + "MetaData").Single();
        metadata.Element(ns + "Key")!.Value = "/IMAGE/INDEX";
        metadata.Element(ns + "Value")!.Value = image.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var userdata = doc.Descendants(ns + "UserData").Single();
        userdata.Element(ns + "ProductKey")?.Remove();
        // Evaluation Server media refuses retail/KMS setup keys; it selects its image by index.
        if (!image.Evaluation) userdata.Add(new XElement(ns + "ProductKey", new XElement(ns + "Key", GenericKey(selection))));
        var script = Resource(family + ".firstlogon.ps1");
        script += "\nnetsh advfirewall firewall add rule name=sshd dir=in action=allow protocol=TCP localport=22 | Out-Null\nreg add 'HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server' /v fDenyTSConnections /t REG_DWORD /d 0 /f | Out-Null\nEnable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue\n";
        script += "\nif (Test-Path \"$PSScriptRoot\\extra.ps1\") { & \"$PSScriptRoot\\extra.ps1\" }\nif (Test-Path \"$PSScriptRoot\\construct-report.ps1\") { & \"$PSScriptRoot\\construct-report.ps1\" }\n";
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["autounattend.xml"] = doc.ToString(), ["firstlogon.ps1"] = script };
        if (u.FirstLogonScript is not null) files.Add("extra.ps1", u.FirstLogonScript);
        foreach (var f in u.Files ?? new Dictionary<string, string>()) if (!files.TryAdd(f.Key, f.Value)) throw Invalid("unattend.files");
        return files;
    }
    public static string GenericKey(WindowsSelection s) => (s.Product, s.Edition.Replace("-core", "")) switch
    {
        ("win11", "pro") => "VK7JG-NPHTM-C97JM-9MPGT-3V66T",
        ("win11", "pro-n") => "2B87N-8KFHP-DKV6R-Y2C8J-PKCKT",
        ("win11", "enterprise") => "NPPR9-FWDCX-D2C8J-H872K-2YT43",
        ("win11", "education") => "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2",
        ("server2022", "standard") => "VDYBN-27WPP-V4HQT-9VMD4-VMK7H",
        ("server2022", "datacenter") => "WX4NM-KYWYW-QJJR4-XV3QB-6VM33",
        ("server2025", "standard") => "TVRH6-WHNXV-R9WG3-9XRFY-MY832",
        ("server2025", "datacenter") => "D764K-2NDRG-47T6Q-P8T8W-YP6DF",
        _ => throw Invalid("windows")
    };
    private static string Resource(string name) { using var stream = typeof(WindowsUnattendRenderer).Assembly.GetManifestResourceStream(name)!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    private static ChildValidationException Invalid(string field) => new("validation", field);
}
