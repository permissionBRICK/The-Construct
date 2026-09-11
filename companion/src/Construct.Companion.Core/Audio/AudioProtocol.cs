using System.Text;
using System.Text.RegularExpressions;
namespace Construct.Companion.Core.Audio;

public static class AudioProtocol
{
    public const int SampleRate = 16000, Channels = 1, BitDepth = 16, BytesPerFrame = 2;
    public static int[] ParseBusyPorts(string stdout)
    {
        var match = Regex.Match(stdout, "CONSTRUCT_PORTS_BUSY=([0-9,]*)");
        return !match.Success ? [] : match.Groups[1].Value.Split(',').Where(s => Regex.IsMatch(s, "^[0-9]{1,5}$"))
            .Select(int.Parse).Where(p => p <= 65535).ToArray();
    }
    public static int[] PortCandidates(IEnumerable<int>? busy = null, int portBase = 8767, int count = 8)
    {
        portBase = NormalizePort(portBase, 8767); count = NormalizeCount(count);
        var taken = (busy ?? []).ToHashSet(); return Enumerable.Range(portBase, Math.Min(count, 65536 - portBase)).Where(p => !taken.Contains(p)).ToArray();
    }
    public static int NormalizePort(int value, int fallback) => value is >= 0 and <= 65535 ? value : fallback;
    public static int NormalizeCount(int value) => value is >= 1 and <= 16 ? value : 8;
    public static bool ConfirmPatched(string token, string stdout) => Regex.IsMatch(stdout, Regex.Escape(token) + "=1(?![0-9])");
    public static string EnableScript(string? enableText = null, string? shimText = null, int port = 8767, int count = 8) => GuestScripts.Render("audio-enable", new Dictionary<string, string>
    { ["port"] = NormalizePort(port, 8767).ToString(System.Globalization.CultureInfo.InvariantCulture), ["count"] = NormalizeCount(count).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["shim"] = B64(shimText ?? GuestScripts.Render("construct-rec-shim")), ["enable"] = B64(enableText ?? GuestScripts.Render("construct-audio-enable")) });
    public static string DisableScript(int self = 0, string? disableText = null, int port = 8767, int count = 8) => GuestScripts.Render("audio-disable", new Dictionary<string, string>
    { ["self"] = NormalizePort(self, 0).ToString(System.Globalization.CultureInfo.InvariantCulture), ["port"] = NormalizePort(port, 8767).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["count"] = NormalizeCount(count).ToString(System.Globalization.CultureInfo.InvariantCulture), ["disable"] = B64(disableText ?? GuestScripts.Render("construct-audio-disable")) });
    private static string B64(string text) => ShellQuote.Single(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));
}
