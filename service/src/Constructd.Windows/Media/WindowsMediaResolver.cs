using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
namespace Constructd.Windows.Media;

public sealed class WindowsMediaResolver(IProcessRunner runner, ConstructdOptions options)
{
    public async Task<Uri> ResolveAsync(string product, string language, CancellationToken ct)
    {
        if (options.Fake) throw new MediaException("windows-resolver-unavailable");
        if (language is null || !Regex.IsMatch(language, @"\A[a-z]{2}(?:-[A-Z]{2})?\z")) throw new MediaException("validation");
        if (product is "server2022" or "server2025")
        {
            if (language is not ("en" or "en-US")) throw new MediaException("windows-language-unsupported");
            return new Uri(product == "server2022"
                ? "https://go.microsoft.com/fwlink/p/?LinkID=2195280&clcid=0x409&culture=en-us&country=US"
                : "https://go.microsoft.com/fwlink/?linkid=2293312&clcid=0x409&culture=en-us&country=us");
        }
        if (product != "win11") throw new MediaException("validation");
        // Pin the resolver source; resolving a current Microsoft URL does not require executing moving code.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var script = await http.GetStringAsync("https://raw.githubusercontent.com/pbatard/Fido/3d47260b8915385c58e20c73e24b36e9a9536f3f/Fido.ps1", ct);
        if (!OperatingSystem.IsWindows())
        {
            script = script.Replace("$Arch = Get-CimInstance -ClassName Win32_Processor | Select-Object -ExpandProperty Architecture", "$Arch = 9");
            script = Regex.Replace(script, @"(?m)^\$winver = .*", "$winver = 10.0");
        }
        var temp = Path.Combine(Path.GetTempPath(), "construct-fido-" + Guid.NewGuid().ToString("n") + ".ps1");
        try
        {
            await File.WriteAllTextAsync(temp, script, ct);
            var result = await runner.RunAsync(OperatingSystem.IsWindows() ? options.PowerShellPath : "pwsh",
                ["-NoProfile", "-NonInteractive", "-File", temp, "-Win", "11", "-Rel", "latest", "-Ed", "Pro", "-Lang", language is "en" or "en-US" ? "eng" : language, "-Arch", "x64", "-GetUrl"],
                null, TimeSpan.FromMinutes(5), null, ct);
            if (!result.Succeeded) throw new MediaException("windows-resolve-failed");
            var url = Regex.Matches(result.StandardOutput, @"https://[^\s]+") .Select(m => m.Value).LastOrDefault();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.IdnHost.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".software-download.prss.microsoft.com", StringComparison.OrdinalIgnoreCase))) throw new MediaException("windows-resolve-failed");
            return uri;
        }
        finally { File.Delete(temp); }
    }
}
