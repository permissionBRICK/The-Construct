using Construct.Companion.Core.Abstractions;
namespace Construct.Companion;

internal sealed class DesktopPrompts(Control dispatcher) : IPrompts
{
    private Task<T> OnUi<T>(Func<T> action,CancellationToken cancellationToken) => dispatcher.InvokeAsync(action,cancellationToken);
    // Dialogs size themselves to their content (owner, 2026-09-12): a fixed 620x320 form left a
    // block of empty space between a two-line message and its buttons, and grew again with DPI.
    // Design units are 96-dpi pixels; AutoScaleMode.Dpi scales them for the monitor.
    private const int ContentWidth=560;
    private static (Form Form,TableLayoutPanel Body) Dialog(string title)
    {
        var form=new Form { Text=title, StartPosition=FormStartPosition.CenterScreen, AutoScaleMode=AutoScaleMode.Dpi, AutoScaleDimensions=new SizeF(96,96),
            AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FormBorderStyle=FormBorderStyle.FixedDialog,
            MinimizeBox=false, MaximizeBox=false, ShowInTaskbar=false, Padding=new Padding(12) };
        var body=new TableLayoutPanel { AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1, Location=new Point(12,12), Margin=new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.Controls.Add(body);
        return (form,body);
    }
    private static Label Text(string text) => new() { AutoSize=true, MaximumSize=new Size(ContentWidth,0), MinimumSize=new Size(ContentWidth,0), Text=text, Margin=new Padding(0,0,0,10) };
    private static void Buttons(TableLayoutPanel body,Button ok,Button? cancel=null,params Button[] extra)
    {
        cancel??=new Button { Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true };
        var bar=new FlowLayoutPanel { AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, FlowDirection=FlowDirection.RightToLeft, Margin=new Padding(0,6,0,0), MinimumSize=new Size(ContentWidth,0), WrapContents=false };
        if (!ReferenceEquals(cancel,ok)) bar.Controls.Add(cancel);
        bar.Controls.Add(ok); foreach (var button in extra) bar.Controls.Add(button);
        body.Controls.Add(bar);
        var form=(Form)body.Parent!; form.AcceptButton=ok; form.CancelButton=cancel;
    }
    private static DialogResult Show(Form form,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancel=cancellationToken.Register(()=> { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(()=>form.Close()); });
        return form.ShowDialog();
    }
    public Task<string?> InputAsync(InputPrompt prompt,CancellationToken cancellationToken=default) => OnUi(() =>
    {
        var (form,body)=Dialog(prompt.Title); using var _=form;
        var field=new TextBox { Width=ContentWidth,Text=prompt.Value ?? "",UseSystemPasswordChar=prompt.Password,PlaceholderText=prompt.Placeholder ?? "",Margin=new Padding(0,0,0,4) };
        body.Controls.Add(Text(prompt.Prompt)); body.Controls.Add(field);
        Buttons(body,new Button { Text="OK",DialogResult=DialogResult.OK,AutoSize=true });
        form.Shown+=(_,_)=>field.Focus();
        return Show(form,cancellationToken)==DialogResult.OK ? field.Text : null;
    },cancellationToken);
    public Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt,CancellationToken cancellationToken=default) => OnUi<IReadOnlyList<string>?>(()=>
    {
        var (form,body)=Dialog(prompt.Title); using var _=form;
        var rows=Math.Clamp(prompt.Items.Count,3,12);
        var list=new ListView { Width=ContentWidth,Height=24*rows+8,View=View.Details,FullRowSelect=true,MultiSelect=prompt.Multiple,CheckBoxes=prompt.Multiple,HideSelection=false,HeaderStyle=ColumnHeaderStyle.None,Margin=new Padding(0,0,0,4) };
        list.Columns.Add("Item",220); list.Columns.Add("Description",340);
        foreach (var item in prompt.Items)
        {
            var row=new ListViewItem(item.Label) { Tag=item,Checked=prompt.Multiple && item.Picked && !item.Disabled && !item.Separator };
            row.SubItems.Add(item.Description ?? ""); if (item.Disabled || item.Separator) row.ForeColor=SystemColors.GrayText; list.Items.Add(row);
            if (!prompt.Multiple && item.Picked && !item.Disabled && !item.Separator) row.Selected=true;
        }
        var ok=new Button { Text="OK",DialogResult=DialogResult.OK,AutoSize=true,Enabled=prompt.Multiple };
        list.ItemCheck+=(_,e)=> { if (list.Items[e.Index].Tag is PickItem { Disabled:true } or PickItem { Separator:true }) e.NewValue=CheckState.Unchecked; };
        list.SelectedIndexChanged+=(_,_)=>ok.Enabled=prompt.Multiple || list.SelectedItems.Cast<ListViewItem>().Any(r=>r.Tag is PickItem { Disabled:false,Separator:false });
        list.DoubleClick+=(_,_)=> { if (!prompt.Multiple && ok.Enabled) { form.DialogResult=DialogResult.OK; form.Close(); } };
        if (prompt.Placeholder is not null) body.Controls.Add(Text(prompt.Placeholder));
        body.Controls.Add(list);
        Buttons(body,ok);
        if (Show(form,cancellationToken)!=DialogResult.OK) return null;
        var chosen=prompt.Multiple ? list.CheckedItems.Cast<ListViewItem>() : list.SelectedItems.Cast<ListViewItem>();
        return chosen.Select(r=>(PickItem)r.Tag!).Where(i=>!i.Disabled && !i.Separator).Select(i=>i.Id).ToArray();
    },cancellationToken);
    public Task<bool> ConfirmAsync(string title,string message,CancellationToken cancellationToken=default) => ConfirmAsync(new ConfirmationPrompt(title, message, "Confirm"), cancellationToken);
    public Task<bool> ConfirmAsync(ConfirmationPrompt prompt,CancellationToken cancellationToken=default) => OnUi(()=>
    {
        var (form,body)=Dialog(prompt.Title); using var _=form; body.Controls.Add(Text(prompt.Message));
        Buttons(body,new Button { Text=prompt.Action,DialogResult=DialogResult.OK,AutoSize=true }); return Show(form,cancellationToken)==DialogResult.OK;
    },cancellationToken);
    public Task ShowSecretOnceAsync(string title, Secret value, string note, CancellationToken cancellationToken=default) => OnUi(()=>
    {
        var (form,body)=Dialog(title); using var _=form;
        var field=new TextBox { Width=ContentWidth,Height=96,Text=value.Reveal(),ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Margin=new Padding(0,0,0,6) };
        var close=new Button { Text="Close",DialogResult=DialogResult.OK,AutoSize=true };
        var copy=new Button { Text="Copy to clipboard",AutoSize=true };
        copy.Click+=(_,_)=> { try { Clipboard.SetText(field.Text); } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(form,"Could not copy: the clipboard is unavailable.",title); } };
        body.Controls.Add(Text(note)); body.Controls.Add(field);
        body.Controls.Add(Text("This is shown once and is not stored anywhere by The Construct."));
        Buttons(body,close,close,copy);
        try { Show(form,cancellationToken); } finally { field.Clear(); }
        return true;
    },cancellationToken);
    // Raised by a VM while the user works elsewhere: on top and in the taskbar, Deny focused, Approve
    // enabled only after a second so a keystroke meant for another window cannot approve it.
    public Task<bool> ApproveAsync(ApprovalPrompt prompt,CancellationToken cancellationToken=default) => OnUi(()=>
    {
        var (form,body)=Dialog(prompt.Title); using var _=form; form.TopMost=true; form.ShowInTaskbar=true;
        body.Controls.Add(Text(prompt.Message));
        var approve=new Button { Text=prompt.Action,DialogResult=DialogResult.OK,AutoSize=true,Enabled=false };
        var deny=new Button { Text=prompt.Deny,DialogResult=DialogResult.Cancel,AutoSize=true };
        Buttons(body,approve,deny); form.AcceptButton=null;
        using var arm=new System.Windows.Forms.Timer { Interval=1000 };
        arm.Tick+=(_,_)=> { arm.Stop(); approve.Enabled=true; };
        form.Shown+=(_,_)=> { form.Activate(); deny.Focus(); arm.Start(); System.Media.SystemSounds.Asterisk.Play(); };
        var result=Show(form,cancellationToken);
        cancellationToken.ThrowIfCancellationRequested(); // closed by the agent's deadline, not by the user
        return result==DialogResult.OK;
    },cancellationToken);
    public Task<IReadOnlyDictionary<string,string>?> DecideFilesAsync(FileDecisionPrompt prompt,CancellationToken cancellationToken=default) => OnUi<IReadOnlyDictionary<string,string>?>(()=>
    {
        var (form,body)=Dialog(prompt.Title); using var _=form; form.TopMost=true; form.ShowInTaskbar=true;
        body.Controls.Add(Text(prompt.Message));
        var labels=new Dictionary<string,string> { [FileDecisionPrompt.Keep]="Keep", [FileDecisionPrompt.Redact]="Redact", [FileDecisionPrompt.Delete]="Delete file" };
        var grid=new DataGridView { Width=ContentWidth+300,Height=Math.Clamp(prompt.Files.Count,3,14)*26+30,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
            AllowUserToResizeRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,Margin=new Padding(0,0,0,4) };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="File",ReadOnly=true,FillWeight=55 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText="Contains",ReadOnly=true,FillWeight=30 });
        var action=new DataGridViewComboBoxColumn { HeaderText="Action",FillWeight=15,DisplayStyle=DataGridViewComboBoxDisplayStyle.DropDownButton };
        foreach (var id in FileDecisionPrompt.Actions) action.Items.Add(labels[id]);
        grid.Columns.Add(action);
        foreach (var file in prompt.Files) { var row=grid.Rows[grid.Rows.Add(file.Path,file.Detail,labels[FileDecisionPrompt.Keep])]; row.Tag=file.Id; row.Cells[0].ToolTipText=file.Path; }
        grid.CurrentCellDirtyStateChanged+=(_,_)=> { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        body.Controls.Add(grid);
        body.Controls.Add(Text("Redact overwrites the secret inside the file and keeps everything else. Keep leaves the file untouched."));
        var all=new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.LeftToRight,Margin=new Padding(0) };
        foreach (var id in FileDecisionPrompt.Actions)
        {
            var set=new Button { Text=labels[id]+" all",AutoSize=true };
            set.Click+=(_,_)=> { foreach (DataGridViewRow row in grid.Rows) row.Cells[2].Value=labels[id]; };
            all.Controls.Add(set);
        }
        body.Controls.Add(all);
        var apply=new Button { Text="Apply",DialogResult=DialogResult.OK,AutoSize=true };
        Buttons(body,apply,new Button { Text="Keep all",DialogResult=DialogResult.Cancel,AutoSize=true }); form.AcceptButton=null;
        form.Shown+=(_,_)=>form.Activate();
        if (Show(form,cancellationToken)!=DialogResult.OK) return null;
        var byLabel=labels.ToDictionary(p=>p.Value,p=>p.Key);
        return grid.Rows.Cast<DataGridViewRow>().ToDictionary(r=>(string)r.Tag!,r=>byLabel.GetValueOrDefault(r.Cells[2].Value as string ?? "",FileDecisionPrompt.Keep));
    },cancellationToken);
    public Task<string?> SaveFileAsync(SaveFilePrompt prompt,CancellationToken cancellationToken=default) => OnUi(()=>
    {
        using var dialog=new SaveFileDialog { Title=prompt.Title,FileName=prompt.DefaultPath ?? "",Filter=prompt.Filter ?? "All files|*.*",OverwritePrompt=true };
        return dialog.ShowDialog()==DialogResult.OK ? dialog.FileName : null;
    },cancellationToken);
}
