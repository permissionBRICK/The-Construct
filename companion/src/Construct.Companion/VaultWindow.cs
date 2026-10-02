using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Vault;
using Construct.Companion.Host.Ipc;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Construct.Companion;

// The Key Vault: media/vault.html in the control panel's design. Every decision lives in VaultView;
// this window only forwards. Its page talks to VaultView in process and to nothing else: the window
// holds no message sink, so no request of the page reaches the dispatcher, IPC or HTTP. Values stay
// native: the add/edit dialog, the clipboard (cleared again after 30 seconds) and the pairing code
// below are the only places a value, username copy or pairing link exists. Closing hides it.
internal sealed class VaultWindow : Form, IVaultWindow
{
    private const string View = "vault";
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly Platform platform;
    private readonly IpcSettings settings;
    private readonly VaultService vault;
    private readonly VaultHosts hosts;
    private readonly VaultView view;
    private readonly System.Windows.Forms.Timer clock = new() { Interval = (int)VaultView.RefreshInterval.TotalMilliseconds };
    private readonly System.Windows.Forms.Timer clipboardClear = new() { Interval = (int)VaultView.ClipboardLifetime.TotalMilliseconds };
    private readonly CancellationTokenSource lifetime = new();
    private string? copied;
    private string? paletteScriptId;
    private bool darkPalette, initialized, ready, exiting;

    public VaultWindow(Platform platform, IpcSettings settings, IPrompts prompts, VaultService vault, VaultHosts hosts)
    {
        this.platform = platform; this.settings = settings; this.vault = vault; this.hosts = hosts;
        view = new(vault, hosts, prompts, this, platform.Clock);
        Text = WebViewDocument.Title(View); AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new(560, 420); StartPosition = FormStartPosition.Manual;
        var saved = settings.Bounds(View) ?? new WindowBounds(120, 100, 1000, 760);
        var work = Screen.FromRectangle(new(saved.X, saved.Y, saved.Width, saved.Height)).WorkingArea;
        var bounds = WindowPlacement.Clamp(saved, new(work.X, work.Y, work.Width, work.Height)); Bounds = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Controls.Add(web);
        vault.Changed += OnChanged; hosts.Changed += OnChanged;
        clock.Tick += (_, _) => PushState(); clock.Start();
        clipboardClear.Tick += (_, _) => ClearClipboard();
        Shown += async (_, _) => { if (!initialized) { initialized = true; await InitializeAsync(); } };
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; SaveBounds(); Hide(); } };
        ResizeEnd += (_, _) => SaveBounds();
    }
    public void Present()
    {
        if (!Visible) Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate();
        PushState();
        _ = RunAsync(new("refresh")); // the hosts' devices and leases, read fresh whenever the window opens
    }
    public void Shutdown() { exiting = true; SaveBounds(); lifetime.Cancel(); Close(); }

    private async Task InitializeAsync()
    {
        try
        {
            await WebViewSurface.InitializeAsync(web, platform, View, () => this);
            await ApplyPaletteAsync();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            web.CoreWebView2.WebMessageReceived += OnMessage;
            web.CoreWebView2.NavigationCompleted += (_, e) => { ready = e.IsSuccess; PushState(); };
            ReloadTheme();
        }
        catch (Exception e)
        {
            // Without the WebView2 runtime the window cannot work at all; say so in place of the control.
            platform.Log.Write(DesktopLogEvent.WindowFailed, e);
            Controls.Clear(); Controls.Add(new Label { Dock = DockStyle.Fill, Text = "The Key Vault window could not start. Check that Microsoft Edge WebView2 Runtime is installed.", Padding = new Padding(20) });
            web.Dispose();
        }
    }
    public void ReloadTheme()
    {
        if (web.CoreWebView2 is null || web.IsDisposed) return;
        var document = WebViewDocument.Render(WebViewSurface.ReadMedia(platform, "vault.html"), "vault.js", settings.Read().UiTheme, WebViewSurface.NewNonce());
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

    // ── page ↔ VaultView ───────────────────────────────────────────────────────
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != WebViewDocument.DocumentUrl(View)) return;
        VaultCommand command;
        try { using var message = JsonDocument.Parse(e.WebMessageAsJson); command = VaultView.Parse(message.RootElement); }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            platform.Log.Write(DesktopLogEvent.BridgeFailed, ex);
            Post(VaultView.DoneMessage("", new("The key vault did not understand this request.", true)));
            return;
        }
        await RunAsync(command);
    }
    private async Task RunAsync(VaultCommand command)
    {
        try
        {
            var notice = await view.ExecuteAsync(command, lifetime.Token);
            if (IsDisposed) return;
            PushState(); Post(VaultView.DoneMessage(command.Action, notice));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { platform.Log.Write(DesktopLogEvent.BridgeFailed, ex); } // the window must outlive a failed request
    }
    private void OnChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(PushState); }
    // A hidden window is brought up to date by Present.
    private void PushState() { if (Visible) Post(view.StateMessage()); }
    private void Post(System.Text.Json.Nodes.JsonObject message)
    {
        if (!ready || IsDisposed || web.IsDisposed || web.CoreWebView2 is null) return;
        web.CoreWebView2.PostWebMessageAsJson(message.ToJsonString(IpcJson.Options));
    }
    private void SaveBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        if (settings.TrySaveBounds(View, new(Left, Top, Width, Height)) is { } failure) platform.Log.Write(DesktopLogEvent.SettingsFailed, failure);
    }

    // ── IVaultWindow: the native side ──────────────────────────────────────────
    public Task EditSecretAsync(VaultSecretView? existing, Func<VaultSecretInput, string?> save, CancellationToken cancellationToken) => this.InvokeAsync(() =>
    {
        using var editor = new SecretEditor(existing, save);
        editor.ShowDialog(this);
    }, cancellationToken);
    public Task<bool> CopyAsync(Secret text, bool sensitive, CancellationToken cancellationToken) => this.InvokeAsync(() =>
    {
        var value = text.Reveal();
        try { Clipboard.SetText(value); } catch (System.Runtime.InteropServices.ExternalException) { return false; }
        // A copied value leaves the clipboard again after 30 seconds unless something replaced it.
        if (sensitive) { copied = value; clipboardClear.Stop(); clipboardClear.Start(); }
        return true;
    }, cancellationToken);
    public Task ShowPairingAsync(VaultPairing pairing, CancellationToken cancellationToken) => this.InvokeAsync(() =>
    {
        using var dialog = new PairingDialog(pairing);
        dialog.ShowDialog(this);
    }, cancellationToken);
    private void ClearClipboard()
    {
        clipboardClear.Stop();
        try { if (copied is not null && Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear(); }
        catch (System.Runtime.InteropServices.ExternalException) { }
        copied = null;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            vault.Changed -= OnChanged; hosts.Changed -= OnChanged;
            lifetime.Cancel(); lifetime.Dispose(); clock.Dispose(); clipboardClear.Dispose(); web.Dispose();
        }
        base.Dispose(disposing);
    }

    // The phone pairing code: one scan stores the approval token on the host's page, then logs the phone
    // into T3 Code. The link holds both single-use tokens, so it is shown here only and cleared on close.
    private sealed class PairingDialog : Form
    {
        private const int QuietZone = 4;
        private string? copied;
        public PairingDialog(VaultPairing pairing)
        {
            Text = "Pair a phone"; AutoScaleMode = AutoScaleMode.Dpi; FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new(12);
            var url = pairing.Url.Reveal();
            var body = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
            var what = pairing.Vm.Length > 0 ? $" and logs it into T3 Code on “{pairing.Vm}”" : "";
            body.Controls.Add(new Label { AutoSize = true, MaximumSize = new(440, 0), Margin = new(0, 0, 0, 8),
                Text = $"Scan this code with the phone's camera. It lets “{pairing.Label}” approve key vault requests of your VMs on {pairing.Host}{what}." });
            var picture = new PictureBox { Image = Render(QrCode.Encode(url)), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new(0, 0, 0, 8) };
            body.Controls.Add(picture);
            var field = new TextBox { Text = url, ReadOnly = true, Multiline = true, Width = 440, Height = 64, ScrollBars = ScrollBars.Vertical };
            body.Controls.Add(field);
            body.Controls.Add(new Label { AutoSize = true, MaximumSize = new(440, 0), ForeColor = SystemColors.GrayText, Margin = new(0, 6, 0, 0),
                Text = "Valid for about 10 minutes and only once." });
            // The host service's own (self-signed) address: say so, and where a host admin sets a proxy instead.
            if (pairing.Note.Length > 0) body.Controls.Add(new Label { AutoSize = true, MaximumSize = new(440, 0), Margin = new(0, 6, 0, 0), Text = pairing.Note });
            var copy = new Button { Text = "Copy link", AutoSize = true }; var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
            copy.Click += (_, _) => { try { Clipboard.SetText(url); copied = url; } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "The clipboard is unavailable.", Text); } };
            var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
            bar.Controls.AddRange([close, copy]);
            Controls.Add(body); Controls.Add(bar); AcceptButton = close; CancelButton = close;
            FormClosed += (_, _) =>
            {
                field.Clear(); picture.Image?.Dispose();
                try { if (copied is not null && Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear(); }
                catch (System.Runtime.InteropServices.ExternalException) { }
            };
        }
        // Dark modules on white with the 4-module quiet zone scanners expect, at a whole-pixel scale.
        private static Bitmap Render(bool[,] modules)
        {
            var rows = modules.GetLength(0); var columns = modules.GetLength(1);
            var size = Math.Max(rows, columns) + 2 * QuietZone; var scale = Math.Max(3, 330 / size);
            var bitmap = new Bitmap(size * scale, size * scale);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                    if (modules[r, c]) graphics.FillRectangle(Brushes.Black, (c + QuietZone) * scale, (r + QuietZone) * scale, scale, scale);
            return bitmap;
        }
    }

    // Add or edit one secret. The value is hidden until "Show" and is only sent on save when it changed.
    private sealed class SecretEditor : Form
    {
        private readonly TextBox name = new() { Width = 420 }, description = new() { Width = 420 }, username = new() { Width = 420 };
        private readonly TextBox value = new() { Width = 420, UseSystemPasswordChar = true };
        private readonly CheckBox show = new() { Text = "Show", AutoSize = true };
        private readonly Label note = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new(420, 0) };
        private readonly bool editing;
        private string current = "";
        private bool changed, syncing;
        public SecretEditor(VaultSecretView? existing, Func<VaultSecretInput, string?> save)
        {
            editing = existing is not null;
            Text = editing ? "Edit secret" : "Add secret"; AutoScaleMode = AutoScaleMode.Dpi; FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new(12); ShowInTaskbar = false;
            var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            void Field(string label, Control control) { grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 6, 8, 0) }); grid.Controls.Add(control); }
            Field("Name", name); Field("Description", description); Field("Username", username);
            var secretRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new(0) };
            var load = new Button { Text = "Load from file…", AutoSize = true };
            secretRow.Controls.AddRange([value, show, load, note]);
            Field("Secret", secretRow);
            var ok = new Button { Text = "Save", AutoSize = true }; var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var bar = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom };
            bar.Controls.AddRange([cancel, ok]);
            Controls.Add(grid); Controls.Add(bar); AcceptButton = ok; CancelButton = cancel;
            if (existing is not null) { name.Text = existing.Name; description.Text = existing.Description; username.Text = existing.Username; }
            note.Text = editing ? "Leave empty to keep the stored value. A new value ends every VM's access to the old one." : "Agents see this value only after you approve a request.";
            value.TextChanged += (_, _) => { if (syncing) return; current = value.Text.Replace("\r\n", "\n", StringComparison.Ordinal); changed = true; };
            show.CheckedChanged += (_, _) => Render();
            load.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog { Title = "Load secret from file" };
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var info = new FileInfo(dialog.FileName);
                if (info.Length > VaultProtocol.MaxSecretBytes) { MessageBox.Show(this, $"The file is larger than {VaultProtocol.MaxSecretBytes / 1024} KiB.", Text); return; }
                current = File.ReadAllText(dialog.FileName); changed = true; Render();
            };
            ok.Click += (_, _) =>
            {
                var error = save(new(name.Text.Trim(), description.Text, username.Text.Trim(), changed && current.Length > 0 ? new Secret(current) : null));
                if (error is null) { DialogResult = DialogResult.OK; Close(); } else MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            FormClosed += (_, _) => { syncing = true; value.Clear(); current = ""; };
            Render();
        }
        // Multi-line values (keys, certificates) are only editable while shown: a masked single-line box would drop the line breaks.
        private void Render()
        {
            syncing = true;
            var multiline = current.Contains('\n', StringComparison.Ordinal);
            value.UseSystemPasswordChar = !show.Checked; value.Multiline = show.Checked; value.ScrollBars = show.Checked ? ScrollBars.Vertical : ScrollBars.None;
            value.Height = show.Checked ? 110 : value.PreferredHeight;
            value.ReadOnly = !show.Checked && multiline;
            value.Text = !show.Checked && multiline ? new string('•', 12) : current.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", show.Checked ? "\r\n" : "", StringComparison.Ordinal);
            value.PlaceholderText = editing && !changed ? "unchanged" : "";
            syncing = false;
        }
    }
}
