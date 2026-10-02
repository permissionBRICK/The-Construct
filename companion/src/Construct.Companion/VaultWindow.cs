using System.Globalization;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Vault;
namespace Construct.Companion;

// The Key Vault: secrets (values stay hidden unless copied or edited), which VM holds what until when,
// scrubs still waiting for their VM, and what the vault did. Every decision lives in VaultService;
// this window only renders it and forwards clicks. Closing hides it.
internal sealed class VaultWindow : Form
{
    private readonly VaultService vault;
    private readonly ListView secrets = List(("Name", 170), ("Username", 140), ("Description", 330), ("Held by VMs", 90), ("Updated", 130));
    private readonly ListView leases = List(("VM", 140), ("Secret", 170), ("Uses left", 80), ("Until", 130), ("Reason", 260), ("Granted", 80));
    private readonly ListView pending = List(("VM", 140), ("Secret", 170), ("Scrub due", 130));
    private readonly ListView activity = List(("Time", 130), ("VM", 140), ("Event", 600));
    private readonly Label problem = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false, Padding = new(0, 0, 0, 6) };
    private readonly Button reset = new() { Text = "Start a new vault…", AutoSize = true, Visible = false };
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 30000 };
    private readonly System.Windows.Forms.Timer clipboardClear = new() { Interval = 30000 };
    private string? copied;
    private bool exiting;

    public VaultWindow(VaultService vault)
    {
        this.vault = vault;
        Text = "Construct Key Vault"; AutoScaleMode = AutoScaleMode.Dpi; Size = new(1000, 640); MinimumSize = new(640, 420); StartPosition = FormStartPosition.CenterScreen;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Secrets", secrets, problem, reset,
            Action("Add…", Add), Action("Edit…", Edit), Action("Delete", Delete), Action("Copy secret", () => Copy(false)), Action("Copy username", () => Copy(true))));
        tabs.TabPages.Add(Page("Access", Split(leases, pending), null, null,
            Action("Revoke access", Revoke), Action("Discard pending scrubs for VM", Discard)));
        tabs.TabPages.Add(Page("Activity", activity, null, null));
        Controls.Add(tabs);
        secrets.DoubleClick += (_, _) => Edit();
        reset.Click += (_, _) => Reset();
        vault.Changed += OnChanged;
        clock.Tick += (_, _) => Reload(); clock.Start();
        clipboardClear.Tick += (_, _) => ClearClipboard();
        FormClosing += (_, e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Reload();
    }
    public void Present() { if (!Visible) Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Reload(); Activate(); }
    public void Shutdown() { exiting = true; Close(); }

    private void OnChanged() { if (IsHandleCreated && !IsDisposed) BeginInvoke(Reload); }
    private void Reload()
    {
        var unavailable = vault.Unavailable;
        problem.Text = unavailable is null ? "" : unavailable + " VMs get an error until you start a new vault; the unreadable file is kept next to it.";
        problem.Visible = reset.Visible = unavailable is not null;
        Fill(secrets, vault.Secrets().Select(s => Row(s.Name, s.Name, s.Username, s.Description, s.ActiveLeases == 0 ? "" : s.ActiveLeases.ToString(CultureInfo.CurrentCulture), Time(s.UpdatedAt))));
        Fill(leases, vault.Leases().Select(l => Row(l.Id, l.Instance, l.Name, l.UsesLeft?.ToString(CultureInfo.CurrentCulture) ?? "unlimited", Time(l.ExpiresAt), l.Reason, l.Origin)));
        Fill(pending, vault.PendingCleanups().Select(c => Row(c.Instance, c.Instance, c.Name, Time(c.DueAt))));
        Fill(activity, vault.Activity().Reverse().Select(a => { var row = Row("", Time(a.At), a.Instance, a.Text); if (a.Warning) row.ForeColor = Color.DarkOrange; return row; }));
    }

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
    private void Revoke() { foreach (var id in leases.SelectedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToArray()) vault.Revoke(id); }
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
    private static Control Split(ListView top, ListView bottom)
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
        split.Panel1.Controls.Add(top); split.Panel2.Controls.Add(bottom);
        split.Panel2.Controls.Add(new Label { Text = "Scrubs waiting for their VM to come online", Dock = DockStyle.Top, AutoSize = true, Padding = new(0, 6, 0, 4) });
        return split;
    }
    private static TabPage Page(string title, Control content, Label? note, Button? fix, params Button[] actions)
    {
        var page = new TabPage(title) { Padding = new(8) };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new(0, 6, 0, 0) };
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
        if (disposing) { vault.Changed -= OnChanged; clock.Dispose(); clipboardClear.Dispose(); }
        base.Dispose(disposing);
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
