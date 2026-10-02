using System.Globalization;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.Vault;
namespace Construct.Companion;

// The Key Vault: secrets (values stay hidden unless copied or edited), which VM holds what until when
// (here and on hosts), scrubs still waiting for their VM, what the vault did, and the hosts it syncs
// with. Every decision lives in VaultService and VaultHosts; this window only renders them and forwards
// clicks. Closing hides it.
internal sealed class VaultWindow : Form
{
    private readonly VaultService vault;
    private readonly VaultHosts hosts;
    private readonly ListView secrets = List(("Name", 170), ("Username", 140), ("Description", 330), ("Held by VMs", 90), ("Updated", 130));
    private readonly ListView leases = List(("VM", 140), ("Host", 130), ("Secret", 170), ("Uses left", 80), ("Until", 130), ("Reason", 260), ("Granted", 80));
    private readonly ListView hostList = List(("Host", 170), ("Vault", 130), ("Last sync", 130), ("VMs online", 80), ("Status", 420));
    private readonly ListView devices = List(("Paired device", 220), ("Paired", 130), ("Last used", 130));
    private readonly ListView pending = List(("VM", 140), ("Secret", 170), ("Scrub due", 130));
    private readonly ListView activity = List(("Time", 130), ("VM", 140), ("Event", 600));
    private readonly Label problem = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false, Padding = new(0, 0, 0, 6) };
    private readonly Button reset = new() { Text = "Start a new vault…", AutoSize = true, Visible = false };
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 30000 };
    private readonly System.Windows.Forms.Timer clipboardClear = new() { Interval = 30000 };
    private readonly CancellationTokenSource lifetime = new();
    private string? copied;
    private bool exiting;

    public VaultWindow(VaultService vault, VaultHosts hosts)
    {
        this.vault = vault; this.hosts = hosts;
        Text = "Construct Key Vault"; AutoScaleMode = AutoScaleMode.Dpi; Size = new(1000, 640); MinimumSize = new(640, 420); StartPosition = FormStartPosition.CenterScreen;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Secrets", secrets, problem, reset,
            Action("Add…", Add), Action("Edit…", Edit), Action("Delete", Delete), Action("Copy secret", () => Copy(false)), Action("Copy username", () => Copy(true))));
        tabs.TabPages.Add(Page("Access", Split(leases, pending), null, null,
            Action("Revoke access", Revoke), Action("Discard pending scrubs for VM", Discard)));
        tabs.TabPages.Add(Page("Activity", activity, null, null));
        tabs.TabPages.Add(Page("Hosts", Split(hostList, devices, "Paired devices of the selected host (phones that approve requests)"), null, null,
            Action("Sync now", () => Run(async ct => { await hosts.SyncAsync(ct); return null; })),
            Action("Always available", () => SetMode(VaultSync.Available)), Action("Locked to my PC", () => SetMode(VaultSync.Locked)),
            Action("Pair a phone…", Pair), Action("Revoke device", RevokeDevice),
            Action("Show vault key…", () => Run(hosts.ShowKeyAsync)), Action("Import vault key…", () => Run(hosts.ImportKeyAsync))));
        Controls.Add(tabs);
        secrets.DoubleClick += (_, _) => Edit();
        hostList.MultiSelect = devices.MultiSelect = false;
        hostList.SelectedIndexChanged += (_, _) => FillDevices(hosts.Views());
        reset.Click += (_, _) => Reset();
        vault.Changed += OnChanged; hosts.Changed += OnChanged;
        clock.Tick += (_, _) => Reload(); clock.Start();
        clipboardClear.Tick += (_, _) => ClearClipboard();
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Reload();
    }
    public void Present()
    {
        if (!Visible) Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Reload(); Activate();
        Run(async ct => { await hosts.RefreshAsync(ct); return null; });
    }
    public void Shutdown() { exiting = true; lifetime.Cancel(); Close(); }

    private void OnChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(Reload); }
    private void Reload()
    {
        var unavailable = vault.Unavailable;
        problem.Text = unavailable is null ? "" : unavailable + " VMs get an error until you start a new vault; the unreadable file is kept next to it.";
        problem.Visible = reset.Visible = unavailable is not null;
        Fill(secrets, vault.Secrets().Select(s => Row(s.Name, s.Name, s.Username, s.Description, s.ActiveLeases == 0 ? "" : s.ActiveLeases.ToString(CultureInfo.CurrentCulture), Time(s.UpdatedAt))));
        Fill(leases, vault.Leases().Select(l => Row("local\n" + l.Id, l.Instance, "this PC", l.Name, Uses(l.UsesLeft), Time(l.ExpiresAt), l.Reason, l.Origin))
            .Concat(hosts.Leases().Select(h => Row($"host\n{h.Slug}\n{h.Lease.Id}", h.Lease.Vm, h.Host, h.Lease.Name, Uses(h.Lease.UsesLeft), Time(h.Lease.ExpiresAt), h.Lease.Reason, h.Lease.Origin))));
        Fill(pending, vault.PendingCleanups().Select(c => Row(c.Instance, c.Instance, c.Name, Time(c.DueAt))));
        Fill(activity, vault.Activity().Reverse().Select(a =>
        { var row = Row("", Time(a.At), a.Host.Length > 0 ? (a.Instance.Length > 0 ? $"{a.Instance} ({a.Host})" : a.Host) : a.Instance, a.Text); if (a.Warning) row.ForeColor = Color.DarkOrange; return row; }));
        var views = hosts.Views();
        Fill(hostList, views.Select(h =>
        {
            var row = Row(h.Slug, h.Host, h.State.Mode switch { VaultSync.Available => "Always available", VaultSync.Locked => "Locked to my PC", _ => "—" },
                h.State.LastSyncAt is { } at ? Time(at) : "never", h.Online.ToString(CultureInfo.CurrentCulture),
                h.State.NeedsKey ? "Needs the vault key: Import vault key…" : h.State.LastError.Length > 0 ? h.State.LastError : h.State.LastSyncAt is null ? "Not synced yet" : "In sync");
            if (h.State.NeedsKey || h.State.LastError.Length > 0) row.ForeColor = Color.DarkOrange;
            return row;
        }));
        FillDevices(views);
    }
    private void FillDevices(IReadOnlyList<VaultHostView> views) => Fill(devices, (views.FirstOrDefault(h => h.Slug == Selected(hostList))?.Devices ?? [])
        .Select(d => Row(d.Id, d.Label, d.CreatedAt is { } c ? Time(c) : "", d.LastUsedAt is { } u ? Time(u) : "never")));
    private static string Uses(int? left) => left?.ToString(CultureInfo.CurrentCulture) ?? "unlimited";

    private void Add() => OpenEditor(null);
    private void Edit() { if (Selected(secrets) is { } name) OpenEditor(name); }
    private void OpenEditor(string? original)
    {
        var current = original is null ? null : vault.Secrets().FirstOrDefault(s => s.Name == original);
        if (original is not null && current is null) return;
        using var editor = new SecretEditor(current, input => { try { vault.Save(input, original); return null; } catch (ArgumentException e) { return e.Message; } });
        editor.ShowDialog(this);
    }
    private void Delete()
    {
        if (Selected(secrets) is not { } name) return;
        if (MessageBox.Show(this, $"Delete “{name}” from the key vault? VMs that hold it lose access and are scrubbed of it.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            vault.Delete(name);
    }
    private void Copy(bool username)
    {
        if (Selected(secrets) is not { } name) return;
        var text = username ? vault.Username(name) : vault.Reveal(name)?.Reveal();
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "The clipboard is unavailable.", Text); return; }
        // A copied secret leaves the clipboard again after 30 seconds unless something replaced it.
        if (!username) { copied = text; clipboardClear.Stop(); clipboardClear.Start(); }
    }
    private void ClearClipboard()
    {
        clipboardClear.Stop();
        try { if (copied is not null && Clipboard.ContainsText() && Clipboard.GetText() == copied) Clipboard.Clear(); }
        catch (System.Runtime.InteropServices.ExternalException) { }
        copied = null;
    }
    private void Revoke()
    {
        foreach (var tag in leases.SelectedItems.Cast<ListViewItem>().Select(i => ((string)i.Tag!).Split('\n')).ToArray())
            if (tag[0] == "local") vault.Revoke(tag[1]);
            else Run(ct => hosts.RevokeLeaseAsync(tag[1], tag[2], ct));
    }
    // Hosts tab: every action runs in Core; this only asks for confirmation and shows the outcome.
    private void SetMode(string mode)
    {
        if (Selected(hostList) is not { } slug) { MessageBox.Show(this, "Select a host first.", Text); return; }
        var question = mode == VaultSync.Locked
            ? "Lock the vault on this host to your PC? It then opens only for VMs this PC starts or connects to, and stays open until that VM stops. VMs started without this PC cannot read secrets."
            : "Make the vault on this host always available? The host keeps the vault key, so your VMs can use secrets (with your approval) while this PC is off.";
        if (MessageBox.Show(this, question, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) Run(ct => hosts.SetModeAsync(slug, mode, ct));
    }
    private void RevokeDevice()
    {
        if (Selected(hostList) is not { } slug || Selected(devices) is not { } id) { MessageBox.Show(this, "Select a host and one of its paired devices.", Text); return; }
        if (MessageBox.Show(this, "Revoke this device? It can no longer approve requests; pair it again to restore it.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            Run(ct => hosts.RevokeDeviceAsync(slug, id, ct));
    }
    private void Pair()
    {
        if (Selected(hostList) is not { } slug) { MessageBox.Show(this, "Select the host whose approvals the phone should answer.", Text); return; }
        Run(async ct =>
        {
            if (await hosts.PairPhoneAsync(slug, ct) is not { } pairing) return null;
            using var dialog = new PairingDialog(pairing);
            dialog.ShowDialog(this);
            return null;
        });
    }
    private async void Run(Func<CancellationToken, Task<string?>> action)
    {
        try { if (await action(lifetime.Token) is { } problem && !IsDisposed) MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            // Host and pairing errors are written for the user and never carry a secret; anything else stays generic.
            if (!IsDisposed) MessageBox.Show(this, e is RemoteApiException or InvalidOperationException ? e.Message : "The key vault could not complete this.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        if (!IsDisposed) Reload();
    }
    private void Discard()
    {
        if (Selected(pending) is not { } instance) return;
        if (MessageBox.Show(this, $"Forget the pending scrubs for “{instance}”? Copies of those secrets may stay on that VM.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
            vault.DiscardCleanups(instance);
    }
    private void Reset()
    {
        if (MessageBox.Show(this, "Start a new, empty key vault? The unreadable file is renamed and kept, so it can still be restored on the PC and account that created it.", Text,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var moved = vault.ResetUnreadable();
        MessageBox.Show(this, "The old vault file was kept as:\n" + moved, Text);
    }

    private static string Time(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture);
    private static string? Selected(ListView list) => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as string;
    private static ListViewItem Row(string tag, params string[] cells)
    { var row = new ListViewItem(cells[0]) { Tag = tag }; foreach (var cell in cells.Skip(1)) row.SubItems.Add(cell); return row; }
    private static void Fill(ListView list, IEnumerable<ListViewItem> rows)
    {
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as string).ToHashSet();
        list.BeginUpdate(); list.Items.Clear();
        foreach (var row in rows) { list.Items.Add(row); if (selected.Contains(row.Tag as string)) row.Selected = true; }
        list.EndUpdate();
    }
    private static ListView List(params (string Title, int Width)[] columns)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true };
        foreach (var (title, width) in columns) list.Columns.Add(title, width);
        return list;
    }
    private static Button Action(string text, Action click) { var button = new Button { Text = text, AutoSize = true }; button.Click += (_, _) => click(); return button; }
    private static Control Split(ListView top, ListView bottom, string caption = "Scrubs waiting for their VM to come online")
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
        split.Panel1.Controls.Add(top); split.Panel2.Controls.Add(bottom);
        split.Panel2.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, AutoSize = true, Padding = new(0, 6, 0, 4) });
        return split;
    }
    private static TabPage Page(string title, Control content, Label? note, Button? fix, params Button[] actions)
    {
        var page = new TabPage(title) { Padding = new(8) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new(0, 6, 0, 0) };
        bar.Controls.AddRange(actions);
        page.Controls.Add(content); page.Controls.Add(bar);
        if (note is not null)
        {
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown };
            top.Controls.Add(note); if (fix is not null) top.Controls.Add(fix);
            page.Controls.Add(top);
        }
        return page;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { vault.Changed -= OnChanged; hosts.Changed -= OnChanged; lifetime.Cancel(); lifetime.Dispose(); clock.Dispose(); clipboardClear.Dispose(); }
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
                Text = "Valid for about 10 minutes and only once. If the host uses its own certificate, the phone warns about it once." });
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
            StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Padding = new(12);
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
