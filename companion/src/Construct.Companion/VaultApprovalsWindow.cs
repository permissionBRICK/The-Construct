using System.Text.Json;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Vault;
using Construct.Companion.Host.Ipc;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Construct.Companion;

// The key vault's tray pop-out: media/approvals.html where the launcher popup appears, in the same design.
// Every decision lives in VaultApprovals; this window only renders and forwards. It appears by itself when
// an approval arrives, without taking the focus, so keystrokes meant for another window cannot reach it;
// it stays until no approval is left, and its × hides it until the next one arrives or the tray brings it
// back. Its page talks to VaultApprovals in process and to nothing else: the window holds no message sink,
// so no request of the page reaches the dispatcher, IPC or HTTP.
internal sealed class VaultApprovalsWindow : Form
{
    private const string View = "approvals";
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Platform platform;
    private readonly IpcSettings settings;
    private readonly VaultService vault;
    private readonly VaultApprovals model;
    private readonly Func<int, Rectangle> place;
    private readonly Action openVault;
    private readonly Action<string> notify;
    private readonly System.Windows.Forms.Timer clock = new() { Interval = (int)VaultApprovals.RefreshInterval.TotalMilliseconds };
    private readonly CancellationTokenSource lifetime = new();
    private string? paletteScriptId;
    private bool darkPalette, initialized, ready, exiting;
    private int pageHeight = VaultApprovals.MinHeight;

    // place: the bounds near the tray for the page's height in CSS pixels. notify: a problem to tell while the pop-out is hidden.
    public VaultApprovalsWindow(Platform platform, IpcSettings settings, VaultService vault, VaultApprovals model, Func<int, Rectangle> place, Action openVault, Action<string> notify)
    {
        this.platform = platform; this.settings = settings; this.vault = vault; this.model = model; this.place = place; this.openVault = openVault; this.notify = notify;
        Text = WebViewDocument.Title(View); AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; TopMost = true; ShowInTaskbar = false;
        Controls.Add(web);
        _ = Handle; // approvals arrive on other threads before the window was ever shown
        vault.ApprovalsChanged += OnApprovalsChanged;
        clock.Tick += (_, _) => PushState();
        VisibleChanged += (_, _) => { if (Visible) clock.Start(); else clock.Stop(); };
        Shown += async (_, _) => { if (!initialized) { initialized = true; await InitializeAsync(); } };
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Apply();
    }
    // Shown by an arriving approval: on top, but the window the user works in keeps the focus.
    protected override bool ShowWithoutActivation => true;
    public int Count => model.Count;
    // Brought back by the user (tray menu or left click): activated.
    public void Present()
    {
        if (model.Count == 0) return;
        Place(); if (!Visible) Show(); Activate(); PushState();
    }
    public void Shutdown() { exiting = true; lifetime.Cancel(); Close(); }

    private void OnApprovalsChanged()
    {
        try { if (!IsDisposed) BeginInvoke(Apply); }
        catch (InvalidOperationException) { /* the window is closing with the app */ }
    }
    private void Apply()
    {
        var change = model.Sync();
        if (change.Hide) { Hide(); return; }
        if (change.Show) { Place(); if (!Visible) Show(); }
        PushState();
    }
    private void Place() => Bounds = place(pageHeight);

    private async Task InitializeAsync()
    {
        try
        {
            await WebViewSurface.InitializeAsync(web, platform, View, () => null);
            await ApplyPaletteAsync();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.NavigationCompleted += (_, e) => { ready = e.IsSuccess; PushState(); };
            ReloadTheme();
        }
        catch (Exception e)
        {
            // Without the WebView2 runtime the pop-out cannot work at all; say where else to answer.
            platform.Log.Write(DesktopLogEvent.WindowFailed, e);
            Controls.Clear(); Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(16),
                Text = "Key vault requests wait for your approval, but this pop-out could not start. Check that Microsoft Edge WebView2 Runtime is installed." });
            web.Dispose();
        }
    }
    public void ReloadTheme()
    {
        if (web.CoreWebView2 is null || web.IsDisposed) return;
        var document = WebViewDocument.Render(WebViewSurface.ReadMedia(platform, "approvals.html"), "approvals.js", settings.Read().UiTheme, WebViewSurface.NewNonce());
        ready = false; WebViewSurface.Load(web.CoreWebView2, platform, View, document);
    }
    private async Task ApplyPaletteAsync()
    {
        darkPalette = platform.IsDarkAppTheme;
        paletteScriptId = await WebViewSurface.ApplyPaletteAsync(web.CoreWebView2, darkPalette, paletteScriptId);
    }
    private async void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || IsDisposed || web.CoreWebView2 is null || darkPalette == platform.IsDarkAppTheme) return;
        try { await ApplyPaletteAsync(); ReloadTheme(); }
        catch (Exception ex) { platform.Log.Write(DesktopLogEvent.WindowFailed, ex); }
    }

    // ── page ↔ VaultApprovals ──────────────────────────────────────────────────
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != WebViewDocument.DocumentUrl(View)) return;
        VaultApprovalsCommand command;
        try { using var message = JsonDocument.Parse(e.WebMessageAsJson); command = VaultApprovals.Parse(message.RootElement); }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { platform.Log.Write(DesktopLogEvent.BridgeFailed, ex); return; }
        try
        {
            switch (command.Action)
            {
                case "ready": PushState(); break;
                case "hide": Hide(); break;
                case "openVault": openVault(); break;
                case "size": pageHeight = command.Height; if (Visible) Place(); break;
                case "decide":
                    var notice = await model.DecideAsync(command, lifetime.Token);
                    if (IsDisposed) return;
                    Post(VaultApprovals.DoneMessage(command.Id, notice));
                    if (notice is not null && !Visible) notify(notice); // the list emptied meanwhile: say it on the tray
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { platform.Log.Write(DesktopLogEvent.BridgeFailed, ex); } // the pop-out must outlive a failed request
    }
    // A hidden pop-out is brought up to date when it shows again.
    private void PushState() { if (Visible) Post(model.StateMessage()); }
    private void Post(System.Text.Json.Nodes.JsonObject message)
    {
        if (!ready || IsDisposed || web.IsDisposed || web.CoreWebView2 is null) return;
        web.CoreWebView2.PostWebMessageAsJson(message.ToJsonString(IpcJson.Options));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            vault.ApprovalsChanged -= OnApprovalsChanged;
            lifetime.Cancel(); lifetime.Dispose(); clock.Dispose(); web.Dispose();
        }
        base.Dispose(disposing);
    }
}
