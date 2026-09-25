using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Microsoft.Extensions.Logging;
namespace Constructd.Windows.Media;

public sealed class WindowsMediaResolver(IProcessRunner runner, ConstructdOptions options, ILogger<WindowsMediaResolver> logger)
{
    // Fido matches -Lang as a regex against Microsoft's English language names; anchor them so
    // "English" cannot pick "English International". Region-specific codes first, then the primary tag.
    private static readonly Dictionary<string, string> Languages = new(StringComparer.Ordinal)
    {
        ["pt-BR"] = "Brazilian Portuguese", ["zh-CN"] = "Chinese (Simplified)", ["zh-TW"] = "Chinese (Traditional)",
        ["en-GB"] = "English International", ["fr-CA"] = "French Canadian", ["es-MX"] = "Spanish (Mexico)",
        ["ar"] = "Arabic", ["bg"] = "Bulgarian", ["zh"] = "Chinese (Simplified)", ["hr"] = "Croatian", ["cs"] = "Czech",
        ["da"] = "Danish", ["nl"] = "Dutch", ["en"] = "English", ["et"] = "Estonian", ["fi"] = "Finnish", ["fr"] = "French",
        ["de"] = "German", ["el"] = "Greek", ["he"] = "Hebrew", ["hu"] = "Hungarian", ["it"] = "Italian", ["ja"] = "Japanese",
        ["ko"] = "Korean", ["lv"] = "Latvian", ["lt"] = "Lithuanian", ["nb"] = "Norwegian", ["no"] = "Norwegian",
        ["pl"] = "Polish", ["pt"] = "Portuguese", ["ro"] = "Romanian", ["ru"] = "Russian", ["sr"] = "Serbian Latin",
        ["sk"] = "Slovak", ["sl"] = "Slovenian", ["es"] = "Spanish", ["sv"] = "Swedish", ["th"] = "Thai", ["tr"] = "Turkish",
        ["uk"] = "Ukrainian",
    };

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
        var fidoLanguage = FidoLanguage(language) ?? throw new MediaException("windows-language-unsupported");
        // Pin the resolver source; resolving a current Microsoft URL does not require executing moving code.
        string script;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            script = await http.GetStringAsync("https://raw.githubusercontent.com/pbatard/Fido/3d47260b8915385c58e20c73e24b36e9a9536f3f/Fido.ps1", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not download the Fido resolver script from raw.githubusercontent.com: {Error}", ex.Message);
            throw new MediaException("windows-resolver-download-failed");
        }
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
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", temp, "-Win", "11", "-Rel", "latest", "-Ed", "Pro", "-Lang", fidoLanguage, "-Arch", "x64", "-GetUrl"],
                null, TimeSpan.FromMinutes(5), null, ct);
            if (!result.Succeeded)
            {
                var reason = FidoFailure(result);
                logger.LogWarning("Resolving the Windows 11 {Language} download failed (exit {ExitCode}): {Reason}", language, result.ExitCode, reason);
                throw new MediaException(IsMicrosoftRejection(reason) ? "windows-download-rejected" : "windows-resolve-failed");
            }
            var url = Regex.Matches(result.StandardOutput, @"https://[^\s]+") .Select(m => m.Value).LastOrDefault();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.IdnHost.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.IdnHost.EndsWith(".software-download.prss.microsoft.com", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogWarning("Fido returned no Microsoft download URL for Windows 11 {Language}.", language);
                throw new MediaException("windows-resolve-failed");
            }
            return uri;
        }
        finally { File.Delete(temp); }
    }

    /// <summary>The anchored Fido -Lang pattern for a language code, or null when Microsoft offers no such ISO.</summary>
    internal static string? FidoLanguage(string language) =>
        Languages.TryGetValue(language, out var name) || Languages.TryGetValue(language[..2], out name) ? "^" + name + "$" : null;

    /// <summary>Fido prints its errors as "Error: …" on stdout; fall back to the last line of output.</summary>
    internal static string FidoFailure(ProcessResult result)
    {
        if (result.TimedOut) return "Fido did not finish within the time limit.";
        var lines = (result.StandardOutput + "\n" + result.StandardError).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var line = lines.LastOrDefault(l => l.StartsWith("Error:", StringComparison.Ordinal)) ?? lines.LastOrDefault() ?? "no output";
        return line.Length > 500 ? line[..500] : line;
    }

    /// <summary>Microsoft's download protection refused the request (Sentinel, or ban code 715-123130).</summary>
    internal static bool IsMicrosoftRejection(string reason) =>
        reason.Contains("Sentinel", StringComparison.OrdinalIgnoreCase) || reason.Contains("715-123130", StringComparison.Ordinal);
}
