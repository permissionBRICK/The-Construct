using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Host.Ipc;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Construct.Companion;

// One WebView2 window per view (panel, settings, popup, hostadmin, theme) hosting the unchanged
// extension media through the bridge shim (§9.4). Closing hides; the process keeps running.
internal sealed class WebViewWindow : Form
{
    private const int PendingLimit = 128; // messages buffered while the document is (re)loading; older ones are superseded by the next snapshot
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Platform platform;
    private readonly IMessageSink sink;
    private readonly IpcSettings settings;
    private readonly Func<WebViewWindow, string, JsonElement, Task<bool>> localMessage;
    private readonly string view;
    private readonly string cacheDirectory;
    private readonly string documentDirectory;
    private readonly List<JsonElement> pending = [];
    private CancellationTokenSource subscription = new();
    private string scope;
    private string document = ""; // the rendered document, written into the mapped folder on every (re)load
    private string? paletteScriptId;
    private bool darkPalette;
    private bool initialized;
    private bool ready;
    private bool openingRefresh = true;
    private bool openingSnapshotOnly;
    private bool exiting;
    public event Action? PopupDeactivated;
    public WebViewWindow(string view, string scope, Platform platform, IMessageSink sink, IpcSettings settings, Func<WebViewWindow, string, JsonElement, Task<bool>> localMessage)
    {
        this.view = view; this.scope = scope; this.platform = platform; this.sink = sink; this.settings = settings; this.localMessage = localMessage;
        cacheDirectory = Path.Combine(platform.StateDirectory, "webview2");
        documentDirectory = Path.Combine(platform.StateDirectory, "windows", view);
        Text = WebViewDocument.Title(view);
        AutoScaleMode = AutoScaleMode.Dpi; Size = new(1080, 800); MinimumSize = new(360, 300); StartPosition = FormStartPosition.Manual;
        if (view == "popup")
        {
            var (width, height) = TrayModel.PopupSize(96);
            FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; TopMost = true; ShowInTaskbar = false; Size = new(width, height);
        }
        else
        {
            var saved = settings.Bounds(view) ?? new WindowBounds(100, 100, 1080, 800);
            var work = Screen.FromRectangle(new(saved.X, saved.Y, saved.Width, saved.Height)).WorkingArea;
            var bounds = WindowPlacement.Clamp(saved, new(work.X, work.Y, work.Width, work.Height)); Bounds = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        Controls.Add(web);
        if (view == "settings")
        {
            var menu = new MenuStrip();
            foreach (var (label, id) in new[] { ("Choose design…", "chooseTheme"), ("Microphone device…", "chooseMicDevice") })
            { var item = new ToolStripMenuItem(label); item.Click += async (_, _) => await localMessage(this, this.scope, JsonSerializer.SerializeToElement(new { type = "command", id })); menu.Items.Add(item); }
            Controls.Add(menu); MainMenuStrip = menu;
        }
        Shown += async (_, _) => { if (!initialized) { initialized = true; await InitializeAsync(); } else await OpenRequestedViewAsync(); };
        Deactivate += (_, _) => { if (view == "popup") { PopupDeactivated?.Invoke(); Hide(); } };
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; SaveBounds(); Hide(); } };
        // A hidden host-admin window stops its subscription so the Host stops polling that host;
        // panel and popup keep listening because their snapshot must be current when they reappear.
        VisibleChanged += (_, _) =>
        {
            if (view != "hostadmin" || !initialized || web.CoreWebView2 is null) return;
            if (!Visible) subscription.Cancel();
            else if (subscription.IsCancellationRequested)
            {
                subscription.Dispose(); subscription = new(); _ = ListenAsync(subscription.Token);
                _ = sink.PostAsync(scope, JsonSerializer.SerializeToElement(new { type = "hostadmin.ready" }, IpcJson.Options), subscription.Token);
            }
        };
        ResizeEnd += (_, _) => SaveBounds();
    }
    private void SaveBounds()
    {
        if (view == "popup" || WindowState != FormWindowState.Normal) return;
        if (settings.TrySaveBounds(view, new(Left, Top, Width, Height)) is { } failure) platform.Log.Write(DesktopLogEvent.SettingsFailed, failure);
    }
    public void Present(bool refreshScheduled = false)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        openingRefresh = !refreshScheduled; openingSnapshotOnly = refreshScheduled;
        Show(); Activate();
        if (ready)
        {
            _ = OpenRequestedViewAsync();
            if (!refreshScheduled && view is "popup" or "panel" or "settings")
                _ = sink.PostAsync(scope, JsonSerializer.SerializeToElement(new { type = "ready", surfaceOpened = true }, IpcJson.Options), subscription.Token);
            openingRefresh = false; openingSnapshotOnly = false;
        }
    }
    public async Task ChangeScopeAsync(string value, bool notify = true)
    {
        if (scope == value) return;
        subscription.Cancel(); subscription.Dispose(); subscription = new(); scope = value; pending.Clear();
        if (web.CoreWebView2 is not null) { _ = ListenAsync(subscription.Token); if (notify) await sink.PostAsync(scope, JsonSerializer.SerializeToElement(new { type = view == "hostadmin" ? "hostadmin.ready" : "ready" }, IpcJson.Options)); }
    }
    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: cacheDirectory);
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            // Bundled media (scripts, styles, fonts, previews) and the per-view rendered document are two mapped folders.
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(WebViewDocument.VirtualHost, platform.MediaDirectory, CoreWebView2HostResourceAccessKind.Allow);
            platform.Files.CreateDirectory(documentDirectory);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(WebViewDocument.AppHost, documentDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDocument.BridgeScript);
            await ApplyPaletteAsync();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            // The window shows exactly one document; every other navigation is an external link for the browser.
            web.CoreWebView2.NavigationStarting += async (_, e) => { if (e.Uri == DocumentUrl) return; e.Cancel = true; await OpenExternalAsync(e.Uri); };
            web.CoreWebView2.NewWindowRequested += async (_, e) => { e.Handled = true; await OpenExternalAsync(e.Uri); };
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += async (_, e) =>
            {
                if (e.Source != DocumentUrl) return;
                try
                {
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    if (message.RootElement.ValueKind != JsonValueKind.Object) return;
                    if (message.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ready")
                    {
                        await sink.PostAsync(scope, JsonSerializer.SerializeToElement(new { type = "ready", surfaceOpened = openingRefresh, snapshotOnly = openingSnapshotOnly }, IpcJson.Options), subscription.Token);
                        openingRefresh = false; openingSnapshotOnly = false;
                    }
                    else if (!await localMessage(this, scope, message.RootElement.Clone())) await sink.PostAsync(scope, message.RootElement.Clone(), subscription.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    // lifecyclePrepared with an error is the envelope every surface renders as a refusal (and clears its spinner).
                    platform.Log.Write(DesktopLogEvent.BridgeFailed, ex);
                    web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "lifecyclePrepared", ok = false, error = "The Companion could not complete this request." }, IpcJson.Options));
                }
            };
            web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                ready = e.IsSuccess;
                if (!ready) return;
                foreach (var message in pending) web.CoreWebView2.PostWebMessageAsJson(message.GetRawText()); pending.Clear();
                await OpenRequestedViewAsync();
            };
            _ = ListenAsync(subscription.Token); ReloadTheme();
        }
        catch (Exception e)
        {
            // Without the WebView2 runtime the window cannot work at all; say so in place of the control.
            platform.Log.Write(DesktopLogEvent.WindowFailed, e);
            Controls.Clear(); Controls.Add(new Label { Dock = DockStyle.Fill, Text = "The Construct window could not start. Check that Microsoft Edge WebView2 Runtime is installed.", Padding = new Padding(20) });
            web.Dispose();
        }
    }
    private async Task OpenExternalAsync(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host is WebViewDocument.VirtualHost or WebViewDocument.AppHost) return;
        try { await platform.Launcher.OpenAsync(uri.AbsoluteUri); }
        catch (Exception e) { platform.Log.Write(DesktopLogEvent.ActivationFailed, e); } // a broken browser association must not close the window
    }
    private string DocumentUrl => WebViewDocument.DocumentUrl(view);
    // The palette follows the Windows app theme; the media's own CSS fallbacks are VS Code's dark values
    // and must never be mixed with a light system palette, so every variable is injected.
    private async Task ApplyPaletteAsync()
    {
        darkPalette = platform.IsDarkAppTheme;
        web.CoreWebView2.Profile.PreferredColorScheme = darkPalette ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        if (paletteScriptId is not null) web.CoreWebView2.RemoveScriptToExecuteOnDocumentCreated(paletteScriptId);
        paletteScriptId = await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDocument.PaletteScript(DesktopPalette.Variables(darkPalette)));
    }
    private async void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || IsDisposed || web.CoreWebView2 is null || darkPalette == platform.IsDarkAppTheme) return;
        try { await ApplyPaletteAsync(); ReloadTheme(); }
        catch (Exception ex) { platform.Log.Write(DesktopLogEvent.WindowFailed, ex); }
    }
    public void ReloadTheme()
    {
        if (web.CoreWebView2 is null || web.IsDisposed) return;
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        string Read(string name) => Encoding.UTF8.GetString(platform.Files.ReadFile(Path.Combine(platform.MediaDirectory, name)) ?? throw new FileNotFoundException("Bundled media is missing."));
        if (view == "theme")
        {
            var cards = JsonSerializer.Deserialize<ThemeCard[]>(Read("theme-cards.json"), IpcJson.Options)!;
            document = WebViewDocument.ThemePicker(Read("theme-picker.html"), nonce, cards.Select(c => c with { PreviewUri = WebViewDocument.Origin + "/theme-previews/" + c.Id + ".png" }));
        }
        else
        {
            var surface = WebViewDocument.Surface(view);
            document = WebViewDocument.Render(Read(surface + ".html"), surface + ".js", settings.Read().UiTheme, nonce);
        }
        platform.Files.WriteFileAtomic(Path.Combine(documentDirectory, WebViewDocument.DocumentFile(view)), Encoding.UTF8.GetBytes(document));
        ready = false; pending.Clear(); web.CoreWebView2.Navigate(DocumentUrl);
    }
    // Settings are the panel's own settings view: the settings window opens it once the document is ready.
    private Task OpenRequestedViewAsync() => ready && view == "settings" ? web.CoreWebView2.ExecuteScriptAsync(WebViewDocument.OpenSettingsScript) : Task.CompletedTask;
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in sink.Subscribe(scope, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested || IsDisposed) return;
                if (ready) web.CoreWebView2.PostWebMessageAsJson(message.GetRawText());
                else if (pending.Count < PendingLimit) pending.Add(message.Clone());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { platform.Log.Write(DesktopLogEvent.BridgeFailed, e); } // the subscription loop must outlive a bad message
    }
    // Shutdown saves and closes for real; Dispose (from the tray) releases the subscription and control.
    public void Shutdown() { exiting = true; SaveBounds(); subscription.Cancel(); Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (disposing) { subscription.Cancel(); subscription.Dispose(); web.Dispose(); }
        base.Dispose(disposing);
    }
}
