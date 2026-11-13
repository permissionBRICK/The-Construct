using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Construct.Agent.Windows;

/// <summary>
/// One surface of the UI in WebView2 (11.2): the pop-out (<c>pop</c>), the lone vault card (<c>alone</c>)
/// or the main window (<c>win</c>). The page is the agent's own loopback origin, opened with the token
/// in the fragment as <c>construct-agent open</c> does for a browser; the UI picks its surface from
/// <c>?surf=</c>. Nothing else may load in it: any other navigation, frame or new window is cancelled,
/// and an http(s) link goes to the default browser instead.
/// </summary>
internal sealed class AgentWindow : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _surface;
    private readonly Uri _origin;
    private readonly string _token;
    private readonly string _profile;
    private readonly Action<string, string?> _open;
    private readonly Action<Exception> _log;
    private bool _initialized;
    private bool _exiting;
    private string? _pendingRoute;

    /// <param name="open">Opens another surface (<c>win</c> with a route) at the shell's request.</param>
    public AgentWindow(string surface, Uri origin, string token, string profile, Action<string, string?> open, Action<Exception> log)
    {
        _surface = surface;
        _origin = origin;
        _token = token;
        _profile = profile;
        _open = open;
        _log = log;
        Text = "Construct";
        AutoScaleMode = AutoScaleMode.Dpi;
        if (Floating)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }
        else
        {
            Size = new(1200, 820);
            MinimumSize = new(480, 360);
            StartPosition = FormStartPosition.CenterScreen;
        }

        Controls.Add(_web);
        Shown += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await InitializeAsync();
        };
        // The pop-out and the lone card close when they lose focus (11.2); the window only hides.
        Deactivate += (_, _) => { if (Floating) Hide(); };
        FormClosing += (_, e) =>
        {
            if (_exiting || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
        };
    }

    public event Action? FocusLost;

    private bool Floating => _surface is "pop" or "alone";

    /// <summary>Shows the surface; a route (<c>#/vault</c>) moves the window's page there.</summary>
    public void Present(string? route = null)
    {
        if (route is not null)
        {
            if (_web.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync("location.hash = " + JsonSerializer.Serialize(route));
            else _pendingRoute = route;
        }

        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    public new void Hide()
    {
        if (Floating && Visible) FocusLost?.Invoke();
        base.Hide();
    }

    private bool Own(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == _origin.Scheme && parsed.Host == _origin.Host && parsed.Port == _origin.Port;

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _profile);
            await _web.EnsureCoreWebView2Async(environment);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;

            core.NavigationStarting += (_, e) =>
            {
                if (Own(e.Uri))
                {
                    // The pop-out's "open the window" navigates to ?surf=win: the window opens instead.
                    if (Floating && new Uri(e.Uri).Query.Contains("surf=win", StringComparison.Ordinal))
                    {
                        e.Cancel = true;
                        var fragment = new Uri(e.Uri).Fragment;
                        Hide();
                        _open("win", fragment.Length > 1 ? fragment : null);
                    }

                    return;
                }

                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.FrameNavigationStarting += (_, e) => { if (!Own(e.Uri)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                OpenExternal(e.Uri);
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += (_, e) =>
            {
                if (!Own(e.Source)) return;
                try
                {
                    using var message = JsonDocument.Parse(e.WebMessageAsJson);
                    var type = message.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "close") Hide();
                    else if (type == "openWindow")
                    {
                        if (Floating) Hide();
                        _open("win", message.RootElement.TryGetProperty("route", out var r) ? r.GetString() : null);
                    }
                }
                catch (JsonException)
                {
                }
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess || _pendingRoute is not { } route) return;
                _pendingRoute = null;
                _ = core.ExecuteScriptAsync("location.hash = " + JsonSerializer.Serialize(route));
            };

            // The token travels in the fragment, which never reaches a server; the UI takes it out at once.
            core.Navigate($"{_origin.GetLeftPart(UriPartial.Authority)}/?surf={_surface}#agent-token={_token}");
        }
        catch (Exception ex)
        {
            _log(ex);
            Controls.Clear();
            Controls.Add(new Label { Dock = DockStyle.Fill, Padding = new Padding(20), Text = "The Construct window could not start. Check that Microsoft Edge WebView2 Runtime is installed." });
            _web.Dispose();
        }
    }

    private void OpenExternal(string? link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || Own(link)) return;
        try { using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { _log(ex); }
    }

    public void Shutdown()
    {
        _exiting = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _web.Dispose();
        base.Dispose(disposing);
    }
}
