using System.Drawing.Drawing2D;
using Construct.Agent.Device;
using Construct.Agent.Update;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Windows;

namespace Construct.Agent.Windows;

/// <summary>
/// The tray (11.2): a single click opens the pop-out by the tray, a double click the window, the menu
/// opens the window, the key vault, start with Windows and quit. While the pop-out is closed and the window
/// is not in front, a new vault request (an approval, a secret an agent asked for, or a file a scrub found still
/// holding a secret) pops the vault card alone, and so does a lost sign-in (S36), once per loss while the agent runs.
/// </summary>
internal sealed class TrayShell : ApplicationContext, IAgentShell
{
    private const int IdleVaultPollMs = 10_000;
    private const int DeferredVaultPollMs = 2_000;
    private readonly Control _dispatcher = new();
    private readonly NotifyIcon _tray = new() { Visible = true, Text = "Construct" };
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _clickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private readonly System.Windows.Forms.Timer _vaultTimer = new() { Interval = IdleVaultPollMs };
    private readonly System.Windows.Forms.Timer _placeTimer = new() { Interval = 2_000 };
    private readonly System.Windows.Forms.Timer _warmTimer = new() { Interval = 1_500 };
    private readonly PopupGesture _gesture = new();
    private readonly Dictionary<string, AgentWindow> _windows = [];
    // The content height each floating surface last reported (CSS pixels): it opens at that size (finding 7).
    private readonly Dictionary<string, double> _contentHeight = [];
    private readonly HashSet<string> _seenFiles = [];
    private readonly HashSet<string> _seenNotices = [];
    private DisplayedVaultItem[] _lastCandidates = [];
    private string[] _lastUnreachable = [];
    private readonly DeviceAgent _agent;
    private readonly DeviceServices _device;
    private readonly Uri _origin;
    private readonly string _token;
    private readonly WebViewRuntime _runtime;
    private readonly Action<Exception> _log;
    private Rectangle? _trayHitArea;
    private int _iconDpi = 96;
    private bool _showOnClick;
    private bool _attention;
    private bool _polling;
    private bool _exiting;

    public TrayShell(DeviceAgent agent, DeviceServices device, Uri origin, string token, string profile, Action<Exception> log)
    {
        _agent = agent;
        _device = device;
        _origin = origin;
        _token = token;
        _runtime = new WebViewRuntime(profile);
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
        _warmTimer.Tick += (_, _) =>
        {
            _warmTimer.Stop();
            WarmPopOut();
        };
        _warmTimer.Start();
        DrawIcon();
    }

    /// <summary>Opens a surface; called from any thread (the API's <c>/api/shell/open</c>, a toast's link).</summary>
    public void Open(string surface, string? route)
    {
        if (_dispatcher.InvokeRequired) _dispatcher.BeginInvoke(() => Show(surface, route));
        else Show(surface, route);
    }

    /// <summary>The surfaces shown now; called from any thread (a self-update opens them again in the new version).</summary>
    public IReadOnlyList<string> Visible()
    {
        if (_dispatcher.InvokeRequired)
        {
            try { return (IReadOnlyList<string>)_dispatcher.Invoke(Visible); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return []; }
        }

        // The window first: the new version shows the pop-out over it.
        return new[] { "win", "pop", "alone" }.Where(Visible).ToArray();
    }

    private AgentWindow GetOrCreate(string surface)
    {
        if (_windows.TryGetValue(surface, out var window)) return window;
        window = new AgentWindow(surface, _origin, _token, _runtime, Open, _log);
        if (surface == "pop") window.FocusLost += () => _gesture.FocusLost((Control.MouseButtons & MouseButtons.Left) != 0 && _trayHitArea is { } area && area.Contains(Cursor.Position));
        if (surface is "pop" or "alone") window.ContentHeight += css => Fit(surface, window, css);
        // Once the user closes the last open surface, switch immediately to any staged automatic update in the background.
        window.VisibleChanged += (_, _) =>
        {
            if (!_exiting && !window.Visible && Visible().Count == 0) _device.Updater.TryAutoInstall();
        };
        _windows[surface] = window;
        return window;
    }

    /// <summary>
    /// Pre-warms the pop-out's WebView2 and page in the background after startup or an automatic update,
    /// so the user's first tray click opens immediately with the VMs already loaded.
    /// </summary>
    private void WarmPopOut()
    {
        if (_windows.ContainsKey("pop")) return;
        var window = GetOrCreate("pop");
        var (width, height) = TrayModel.PopupSize(_iconDpi);
        if (_contentHeight.TryGetValue("pop", out var css)) height = TrayModel.FittedHeight(css, _iconDpi, window.Zoom);
        window.Bounds = PopupBounds(width, height);
        window.WarmUp();
    }

    private void Show(string surface, string? route)
    {
        Renew(surface);
        var window = GetOrCreate(surface);

        if (surface is "pop" or "alone")
        {
            // Sized to what the page showed last; before the page has measured, the old fixed size.
            var (width, height) = TrayModel.PopupSize(_iconDpi);
            if (_contentHeight.TryGetValue(surface, out var css)) height = TrayModel.FittedHeight(css, _iconDpi, window.Zoom);
            else if (surface == "alone") height = Math.Min(height, (int)(320 * _iconDpi / 96d));
            window.Bounds = PopupBounds(width, height);
        }

        if (surface != "alone") Hide("alone");
        _attention = false;
        DrawIcon();
        window.Present(route);
        // Check after presenting so Visible("pop") is already true and an automatic update stages silently without closing the pop-out.
        if (surface == "pop") _device.Updater.CheckIfStale();
    }

    /// <summary>A floating surface's page measured its content: the window takes that height, still at the tray.</summary>
    private void Fit(string surface, AgentWindow window, double css)
    {
        _contentHeight[surface] = css;
        if (window.Visible) window.Bounds = PopupBounds(window.Width, TrayModel.FittedHeight(css, _iconDpi, window.Zoom));
    }

    /// <summary>
    /// Before a surface opens: a surface whose WebView2 did not start is replaced, and when Windows has
    /// installed a newer WebView2 runtime, or a surface is broken, and no working surface shows, every
    /// surface and the shared environment go, so the next ones start on the runtime that is installed now.
    /// </summary>
    private void Renew(string surface)
    {
        var broken = _windows.Values.Any(w => w.Broken);
        if ((_runtime.Stale || broken) && !_windows.Values.Any(w => w.Visible && !w.Broken))
        {
            foreach (var window in _windows.Values.ToArray()) Drop(window);
            _windows.Clear();
            _runtime.Reset();
            if (surface != "pop") _warmTimer.Start();
        }
        else if (_windows.TryGetValue(surface, out var window) && window.Broken)
        {
            Drop(window);
            _windows.Remove(surface);
        }
    }

    private static void Drop(AgentWindow window)
    {
        window.Shutdown();
        window.Dispose();
    }

    private void Hide(string surface)
    {
        if (_windows.TryGetValue(surface, out var window) && window.Visible) window.Hide();
    }

    private bool Visible(string surface) => _windows.TryGetValue(surface, out var window) && window.Visible;

    private bool InFront(string surface) =>
        _windows.TryGetValue(surface, out var window) && window.Visible && window.WindowState != FormWindowState.Minimized && Form.ActiveForm == window;

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

    /// <summary>Asks the engines for vault requests and lost sign-ins; a new one, with both surfaces closed, pops its card alone.</summary>
    private async Task PollVaultAsync()
    {
        if (!_agent.Device().Registered) return;
        // Re-evaluate already known deferred requests even if an unrelated slow engine poll is still in flight.
        if (_polling)
        {
            EvaluateKnownCandidates();
            return;
        }

        _polling = true;
        try
        {
            var approvals = Task.Run(() => _agent.ApprovalsAsync(CancellationToken.None));
            var entries = Task.Run(() => _agent.EntryRequestsAsync(CancellationToken.None));
            var files = Task.Run(() => _agent.VaultFilesAsync(CancellationToken.None));
            var noticesTask = Task.Run(() => _agent.AgentNoticesAsync(CancellationToken.None));
            await Task.WhenAll(approvals, entries, files, noticesTask);
            var displayedCandidates = new List<DisplayedVaultItem>(approvals.Result.Approvals.Count + entries.Result.Requests.Count);
            displayedCandidates.AddRange(approvals.Result.Approvals.Select(a => DisplayedVaultItem.Approval(a.Engine, a.Id)));
            displayedCandidates.AddRange(entries.Result.Requests.Select(r => DisplayedVaultItem.EntryRequest(r.Engine, r.Id)));
            var unreachable = approvals.Result.Unreachable.Concat(entries.Result.Unreachable).ToArray();
            _lastCandidates = displayedCandidates.ToArray();
            _lastUnreachable = unreachable;

            var evaluation = _agent.VaultDisplay.Evaluate(_lastCandidates, _lastUnreachable);
            var nextInterval = evaluation.Deferred.Count > 0 ? DeferredVaultPollMs : IdleVaultPollMs;
            if (_vaultTimer.Interval != nextInterval) _vaultTimer.Interval = nextInterval;
            // A file a scrub found still holding a secret waits until answered: it pops once per agent run.
            // Ids are never forgotten during a run: one whose engine was briefly out of reach must not pop again.
            var freshFiles = files.Result.Files.Select(f => "f:" + f.Engine + "/" + f.Id).Where(_seenFiles.Add).ToArray();
            // A lost sign-in pops once: its id carries the time of the loss, so only a new loss pops again.
            var notices = noticesTask.Result.Notices
                .Select(n => "n:" + n.Engine + "/" + n.Vm + "/" + n.Agent + "@" + n.At).Where(_seenNotices.Add).ToArray();
            // The open window shows the card on every page, but only to someone looking at it: behind other windows or
            // minimized, the card pops alone all the same (finding 43).
            if (Visible("pop") || InFront("win"))
            {
                if (evaluation.NewlyEligible.Count > 0) _agent.VaultDisplay.MarkPopped(evaluation.NewlyEligible);
                return;
            }

            var poppedApprovals = evaluation.NewlyEligible.Count > 0 ? _agent.VaultDisplay.MarkPopped(evaluation.NewlyEligible) : [];
            if (poppedApprovals.Count > 0 || freshFiles.Length > 0 || notices.Length > 0)
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

    private void EvaluateKnownCandidates()
    {
        if (_lastCandidates.Length == 0) return;
        var evaluation = _agent.VaultDisplay.Evaluate(_lastCandidates, _lastUnreachable);
        var nextInterval = evaluation.Deferred.Count > 0 ? DeferredVaultPollMs : IdleVaultPollMs;
        if (_vaultTimer.Interval != nextInterval) _vaultTimer.Interval = nextInterval;
        if (evaluation.NewlyEligible.Count == 0) return;
        if (Visible("pop") || InFront("win"))
        {
            _agent.VaultDisplay.MarkPopped(evaluation.NewlyEligible);
            return;
        }

        if (_agent.VaultDisplay.MarkPopped(evaluation.NewlyEligible).Count > 0)
        {
            Show("alone", null);
            _attention = true;
            DrawIcon();
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
        _menu.Items.Add("Quit", null, (_, _) =>
        {
            QuitReason ??= "Quit in the tray menu";
            ExitThread();
        });
    }

    /// <summary>A green disc (the Companion's tray colour), with a yellow dot while a popped card waits (11.2: only the vault and a lost sign-in ask for attention).</summary>
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

    /// <summary>
    /// Gives the icon its first place (<see cref="TrayIconCarryOver"/>): asks every two seconds, for up to a minute, until
    /// Windows has an entry for the icon. When the entry changed, the icon is added again so the taskbar reads it.
    /// </summary>
    public void KeepTrayPlace(Func<TrayIconCarryOver.Outcome> apply)
    {
        var left = 30;
        _placeTimer.Tick += (_, _) =>
        {
            try
            {
                var outcome = apply();
                if (outcome == TrayIconCarryOver.Outcome.NotYet && --left > 0) return;
                _placeTimer.Stop();
                if (outcome != TrayIconCarryOver.Outcome.Changed) return;
                _tray.Visible = false;
                _tray.Visible = true;
            }
            catch (Exception ex)
            {
                _placeTimer.Stop();
                _log(ex);
            }
        };
        _placeTimer.Start();
    }

    /// <summary>Why the message loop ended when the user ended it (the tray menu's Quit); null otherwise.</summary>
    public string? QuitReason { get; private set; }

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
        _exiting = true;
        _vaultTimer.Stop();
        _placeTimer.Stop();
        _warmTimer.Stop();
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
            _placeTimer.Dispose();
            _warmTimer.Dispose();
            _dispatcher.Dispose();
        }

        base.Dispose(disposing);
    }
}
