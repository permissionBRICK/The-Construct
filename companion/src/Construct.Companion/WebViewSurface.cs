using System.Security.Cryptography;
using System.Text;
using Construct.Companion.Core.Desktop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Construct.Companion;

// What every Construct WebView2 window shares (§9.4): one profile cache, no developer surfaces, page
// dialogs as native message boxes, the bundled media and the window's own document folder, the bridge
// shim, the Windows palette, and every navigation other than the document handed to the browser.
// Message handling stays with each window.
internal static class WebViewSurface
{
    public static async Task InitializeAsync(WebView2 web, Platform platform, string view, Func<IWin32Window?> owner)
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(platform.StateDirectory, "webview2"));
        await web.EnsureCoreWebView2Async(environment);
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // A page's alert() becomes a native message box: WebView2's own dialog is sized for a
        // browser tab and gets clipped inside the popup. The popup hides on deactivation, so
        // its box has no owner.
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.ScriptDialogOpening += (_, e) =>
        {
            using var deferral = e.GetDeferral();
            try
            {
                if (e.Kind == CoreWebView2ScriptDialogKind.Alert) MessageBox.Show(owner(), e.Message, WebViewDocument.Title(view), MessageBoxButtons.OK, MessageBoxIcon.Information);
                else if (e.Kind == CoreWebView2ScriptDialogKind.Confirm && MessageBox.Show(owner(), e.Message, WebViewDocument.Title(view), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                e.Accept();
            }
            catch (Exception ex) { platform.Log.Write(DesktopLogEvent.BridgeFailed, ex); }
        };
        // Bundled media (scripts, styles, fonts, previews) and the per-view rendered document are two mapped folders.
        core.SetVirtualHostNameToFolderMapping(WebViewDocument.VirtualHost, platform.MediaDirectory, CoreWebView2HostResourceAccessKind.Allow);
        platform.Files.CreateDirectory(DocumentDirectory(platform, view));
        core.SetVirtualHostNameToFolderMapping(WebViewDocument.AppHost, DocumentDirectory(platform, view), CoreWebView2HostResourceAccessKind.DenyCors);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDocument.BridgeScript);
        // The window shows exactly one document; every other navigation is an external link for the browser.
        var document = WebViewDocument.DocumentUrl(view);
        core.NavigationStarting += async (_, e) => { if (e.Uri == document) return; e.Cancel = true; await OpenExternalAsync(platform, e.Uri); };
        core.NewWindowRequested += async (_, e) => { e.Handled = true; await OpenExternalAsync(platform, e.Uri); };
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
    }
    private static string DocumentDirectory(Platform platform, string view) => Path.Combine(platform.StateDirectory, "windows", view);
    private static async Task OpenExternalAsync(Platform platform, string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host is WebViewDocument.VirtualHost or WebViewDocument.AppHost) return;
        try { await platform.Launcher.OpenAsync(uri.AbsoluteUri); }
        catch (Exception e) { platform.Log.Write(DesktopLogEvent.ActivationFailed, e); } // a broken browser association must not close the window
    }
    // The palette follows the Windows app theme; the media's own CSS fallbacks are VS Code's dark values
    // and must never be mixed with a light system palette, so every variable is injected. Returns the
    // script id that replaces previousScript.
    public static async Task<string> ApplyPaletteAsync(CoreWebView2 core, bool dark, string? previousScript)
    {
        core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        if (previousScript is not null) core.RemoveScriptToExecuteOnDocumentCreated(previousScript);
        return await core.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDocument.PaletteScript(DesktopPalette.Variables(dark)));
    }
    public static string ReadMedia(Platform platform, string name) =>
        Encoding.UTF8.GetString(platform.Files.ReadFile(Path.Combine(platform.MediaDirectory, name)) ?? throw new FileNotFoundException("Bundled media is missing."));
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    // Writes the rendered document into the window's mapped folder and loads it.
    public static void Load(CoreWebView2 core, Platform platform, string view, string document)
    {
        platform.Files.WriteFileAtomic(Path.Combine(DocumentDirectory(platform, view), WebViewDocument.DocumentFile(view)), Encoding.UTF8.GetBytes(document));
        core.Navigate(WebViewDocument.DocumentUrl(view));
    }
}
