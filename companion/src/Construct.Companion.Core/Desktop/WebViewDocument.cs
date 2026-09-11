using System.Text.RegularExpressions;
namespace Construct.Companion.Core.Desktop;

public static class WebViewDocument
{
    public const string VirtualHost = "construct.media";
    public const string Origin = "https://" + VirtualHost;
    public const string BridgeScript = """
        (() => {
          let state;
          const api = Object.freeze({postMessage: m => window.chrome.webview.postMessage(m),
            getState: () => state, setState: s => { state = s; return s; }});
          Object.defineProperty(window, 'acquireVsCodeApi', {value: () => api, writable: false});
          window.chrome.webview.addEventListener('message', e => window.dispatchEvent(new MessageEvent('message', {data: e.data})));
        })();
        """;
    public const string OpenSettingsScript = "document.getElementById('gearBtn')?.click();";
    public static bool IsKnownTheme(string? theme) => theme is "classic" or "terminal" or "native";
    public static string ThemeCss(string? theme) => "themes/" + (IsKnownTheme(theme?.Trim().ToLowerInvariant()) ? theme!.Trim().ToLowerInvariant() : "native") + ".css";
    public static string Title(string view) => view switch { "settings" => "Construct Settings", "hostadmin" => "Host Administration", "theme" => "Choose Construct Design", _ => "Construct Companion" };
    // Which media surface a window shows: the popup is the sidebar launcher, settings live inside the panel.
    public static string Surface(string view) => view switch { "popup" => "launcher", "hostadmin" => "hostadmin", _ => "panel" };
    // The native theme reads VS Code CSS variables; the app supplies the system colours for them.
    public static string PaletteScript(IReadOnlyDictionary<string, string> variables) =>
        "window.addEventListener('DOMContentLoaded',()=>{for(const [k,v] of Object.entries(" + System.Text.Json.JsonSerializer.Serialize(variables) + ")) document.documentElement.style.setProperty(k,v);});";
    public static string Render(string template, string script, string? theme, string nonce)
    {
        if (!Regex.IsMatch(nonce, @"\A[A-Za-z0-9+/=]{16,128}\z")) throw new ArgumentException("Invalid document nonce.");
        if (script is not ("panel.js" or "launcher.js" or "hostadmin.js")) throw new ArgumentException("Unknown media script.");
        var substitutions = new Dictionary<string, string>
        {
            ["cspSource"] = Origin, ["nonce"] = nonce, ["styleUri"] = Origin + "/panel.css",
            ["themeUri"] = Origin + "/" + ThemeCss(theme), ["scriptUri"] = Origin + "/" + script,
            ["adminStyleUri"] = Origin + "/hostadmin.css"
        };
        return Regex.Replace(template, @"\{\{(\w+)\}\}", m => substitutions.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }
    public static string ThemePicker(string template, string nonce, IEnumerable<ThemeCard> cards)
    {
        static string Escape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);
        var html = string.Join("\n", cards.Select(c => "<button class=\"card\" data-theme=\"" + Escape(c.Id) + "\">" +
            "<img src=\"" + Escape(c.PreviewUri) + "\" alt=\"Preview of the " + Escape(c.Label) + " design\" />" +
            "<span class=\"card-label\">" + Escape(c.Label) + "</span><span class=\"card-blurb\">" + Escape(c.Blurb) +
            "</span><span class=\"card-cta\">Use this design</span></button>"));
        return Render(template, "panel.js", null, nonce).Replace("{{cardHtml}}", html, StringComparison.Ordinal);
    }
}
public sealed record ThemeCard(string Id, string Label, string Blurb, string PreviewUri);
