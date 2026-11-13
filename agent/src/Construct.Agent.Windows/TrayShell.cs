using System.Drawing.Drawing2D;
using Construct.Agent.Device;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Windows;

namespace Construct.Agent.Windows;

/// <summary>
/// The tray (11.2): a single click opens the pop-out by the tray, a double click the window, the menu
/// opens the window, the key vault, start with Windows and quit. While both the pop-out and the window
/// are closed, a new vault request (an approval or a secret an agent asked for) pops the vault card alone.
/// </summary>
internal sealed class TrayShell : ApplicationContext, IAgentShell
{
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray = new() { Visible = true, Text = "Construct" };
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _clickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private readonly System.Windows.Forms.Timer _vaultTimer = new() { Interval = 10_000 };
    private readonly PopupGesture _gesture = new();
    private readonly Dictionary<string, AgentWindow> _windows = [];
    private readonly HashSet<string> _seenRequests = [];
    private readonly DeviceAgent _agent;
    private readonly DeviceServices _device;
    private readonly Uri _origin;
    private readonly string _token;
    private readonly string _profile;
    private readonly Action<Exception> _log;
    private Rectangle? _trayHitArea;
    private int _iconDpi = 96;
    private bool _showOnClick;
    private bool _attention;
    private bool _polling;

    public TrayShell(DeviceAgent agent, DeviceServices device, Uri origin, string token, string profile, Action<Exception> log)
    {
        _agent = agent;
        _device = device;
        _origin = origin;
        _token = token;
        _profile = profile;
        _log = log;
        _ = _dispatcher.Handle;
        _tray.ContextMenuStrip = _menu;
        _menu.Opening += (_, _) => BuildMenu();
        _tray.MouseDown += (_, e) =>
        {
            var point = Cursor.Position;
            var dpi = DisplayDpi.At(point.X, point.Y);
            var size = TrayModel.IconSize(dpi);
            _trayHitArea = new(point.X - size, point.Y - size, size * 2, size * 2);
            if (dpi != _iconDpi)
            {
                _iconDpi = dpi;
                DrawIcon();
            }

            if (e.Button == MouseButtons.Left) _gesture.Press(_windows.TryGetValue("pop", out var pop) && pop.Visible);
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _showOnClick = _gesture.Click();
            _clickTimer.Stop();
            _clickTimer.Start();
        };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _clickTimer.Stop();
            _gesture.Reset();
            Hide("pop");
            Show("win", null);
        };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (_showOnClick) Show("pop", null);
            else Hide("pop");
        };
        _vaultTimer.Tick += async (_, _) => await PollVaultAsync();
        _vaultTimer.Start();
        DrawIcon();
    }

    /// <summary>Opens a surface; called from any thread (the API's <c>/api/shell/open</c>, a toast's link).</summary>
    public void Open(string surface, string? route)
    {
        if (_dispatcher.InvokeRequired) _dispatcher.BeginInvoke(() => Show(surface, route));
        else Show(surface, route);
    }

    private void Show(string surface, string? route)
    {
        if (!_windows.TryGetValue(surface, out var window))
        {
            window = new AgentWindow(surface, _origin, _token, _profile, Open, _log);
            if (surface == "pop") window.FocusLost += () => _gesture.FocusLost((Control.MouseButtons & MouseButtons.Left) != 0 && _trayHitArea is { } area && area.Contains(Cursor.Position));
            _windows[surface] = window;
        }

        if (surface is "pop" or "alone")
        {
            var (width, height) = TrayModel.PopupSize(_iconDpi);
            window.Bounds = PopupBounds(width, surface == "alone" ? Math.Min(height, (int)(320 * _iconDpi / 96d)) : height);
        }

        if (surface != "alone") Hide("alone");
        _attention = false;
        DrawIcon();
        window.Present(route);
    }

    private void Hide(string surface)
    {
        if (_windows.TryGetValue(surface, out var window) && window.Visible) window.Hide();
    }

    private bool Visible(string surface) => _windows.TryGetValue(surface, out var window) && window.Visible;

    /// <summary>Where the pop-out appears: at the tray icon last clicked, else the bottom right of the primary screen.</summary>
    private Rectangle PopupBounds(int width, int height)
    {
        var screen = _trayHitArea is { } hit ? Screen.FromRectangle(hit) : Screen.PrimaryScreen!;
        var work = screen.WorkingArea;
        var point = _trayHitArea is { } anchor ? new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2) : new Point(work.Right - 16, work.Bottom + 16);
        var icon = TrayModel.IconSize(_iconDpi);
        var bounds = WindowPlacement.Popup(new(point.X - icon / 2, point.Y - icon / 2, icon, icon), new(work.X, work.Y, work.Width, work.Height), width, height);
        return new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>Asks the engines for vault requests; a new one, with both surfaces closed, pops the vault card alone.</summary>
    private async Task PollVaultAsync()
    {
        if (_polling || !_agent.Device().Registered) return;
        _polling = true;
        try
        {
            var ids = new List<string>();
            ids.AddRange((await Task.Run(() => _agent.ApprovalsAsync(CancellationToken.None))).Approvals.Select(a => "a:" + a.Engine + "/" + a.Id));
            ids.AddRange((await Task.Run(() => _agent.EntryRequestsAsync(CancellationToken.None))).Requests.Select(r => "e:" + r.Engine + "/" + r.Id));
            var fresh = ids.Where(_seenRequests.Add).ToArray();
            _seenRequests.IntersectWith(ids);
            if (fresh.Length > 0 && !Visible("pop") && !Visible("win"))
            {
                Show("alone", null);
                _attention = true;
                DrawIcon();
            }
        }
        catch (Exception ex) when (ex is AgentFailure or HttpRequestException or OperationCanceledException)
        {
        }
        finally
        {
            _polling = false;
        }
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Open Construct", null, (_, _) => Show("win", null));
        _menu.Items.Add("Key vault", null, (_, _) => Show("win", "#/vault"));
        if (_device.Platform.StartWithOs is { } start && _device.StartExecutable is { } executable)
        {
            var item = new ToolStripMenuItem("Start with Windows") { Checked = start };
            item.Click += (_, _) =>
            {
                try { _device.Platform.SetStartWithOs(!start, executable); }
                catch (Exception ex) { _log(ex); }
            };
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => ExitThread());
    }

    /// <summary>A green disc (the Companion's tray colour), with a yellow dot while a vault request waits (11.2: only the vault asks for attention).</summary>
    private void DrawIcon()
    {
        var size = TrayModel.IconSize(_iconDpi);
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(61, 211, 107));
            graphics.FillEllipse(brush, 2, 2, size - 4, size - 4);
            if (_attention)
            {
                using var dot = new SolidBrush(Color.FromArgb(242, 195, 55));
                graphics.FillEllipse(Brushes.White, size * 0.55f, size * 0.55f, size * 0.43f, size * 0.43f);
                graphics.FillEllipse(dot, size * 0.6f, size * 0.6f, size * 0.33f, size * 0.33f);
            }
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            var previous = _tray.Icon;
            _tray.Icon = (Icon)borrowed.Clone();
            previous?.Dispose();
        }
        finally
        {
            IconHandle.DestroyIcon(handle);
        }
    }

    /// <summary>Closes every window and ends the message loop; called on the UI thread or marshalled to it.</summary>
    public void Quit()
    {
        if (_dispatcher.InvokeRequired)
        {
            _dispatcher.BeginInvoke(Quit);
            return;
        }

        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _vaultTimer.Stop();
        _tray.Visible = false;
        foreach (var window in _windows.Values) window.Shutdown();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var window in _windows.Values) window.Dispose();
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _menu.Dispose();
            _clickTimer.Dispose();
            _vaultTimer.Dispose();
            _dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }
}
