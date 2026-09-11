using Construct.Companion.Core.Abstractions;
namespace Construct.Companion;

internal sealed class DesktopPrompts(Control dispatcher) : IPrompts
{
    private Task<T> OnUi<T>(Func<T> action,CancellationToken cancellationToken) => dispatcher.InvokeAsync(action,cancellationToken);
    private static Form Dialog(string title)
    {
        return new Form { Text=title, StartPosition=FormStartPosition.CenterScreen, AutoScaleMode=AutoScaleMode.Dpi,
            Size=new(620,320),MinimizeBox=false,MaximizeBox=false,ShowInTaskbar=false };
    }
    private static void Buttons(Form form,Button ok)
    {
        var cancel=new Button { Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true };
        var bar=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=45,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(6) };
        bar.Controls.Add(cancel); bar.Controls.Add(ok); form.Controls.Add(bar); form.AcceptButton=ok; form.CancelButton=cancel;
    }
    private static DialogResult Show(Form form,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cancel=cancellationToken.Register(()=> { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(()=>form.Close()); });
        return form.ShowDialog();
    }
    public Task<string?> InputAsync(InputPrompt prompt,CancellationToken cancellationToken=default) => OnUi(() =>
    {
        using var form=Dialog(prompt.Title);
        var field=new TextBox { Dock=DockStyle.Top,Text=prompt.Value ?? "",UseSystemPasswordChar=prompt.Password,PlaceholderText=prompt.Placeholder ?? "",Margin=new Padding(12) };
        form.Controls.Add(field); form.Controls.Add(new Label { Dock=DockStyle.Top,Text=prompt.Prompt,Height=80,Padding=new Padding(10) });
        Buttons(form,new Button { Text="OK",DialogResult=DialogResult.OK,AutoSize=true });
        form.Shown+=(_,_)=>field.Focus();
        return Show(form,cancellationToken)==DialogResult.OK ? field.Text : null;
    },cancellationToken);
    public Task<IReadOnlyList<string>?> PickAsync(PickPrompt prompt,CancellationToken cancellationToken=default) => OnUi<IReadOnlyList<string>?>(()=>
    {
        using var form=Dialog(prompt.Title); form.Height=480;
        var list=new ListView { Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=prompt.Multiple,CheckBoxes=prompt.Multiple,HideSelection=false,HeaderStyle=ColumnHeaderStyle.None };
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
        form.Controls.Add(list);
        if (prompt.Placeholder is not null) form.Controls.Add(new Label { Dock=DockStyle.Top,Text=prompt.Placeholder,Height=50,Padding=new Padding(10) });
        Buttons(form,ok);
        if (Show(form,cancellationToken)!=DialogResult.OK) return null;
        var chosen=prompt.Multiple ? list.CheckedItems.Cast<ListViewItem>() : list.SelectedItems.Cast<ListViewItem>();
        return chosen.Select(r=>(PickItem)r.Tag!).Where(i=>!i.Disabled && !i.Separator).Select(i=>i.Id).ToArray();
    },cancellationToken);
    public Task<bool> ConfirmAsync(string title,string message,CancellationToken cancellationToken=default) => ConfirmAsync(new ConfirmationPrompt(title, message, "Confirm"), cancellationToken);
    public Task<bool> ConfirmAsync(ConfirmationPrompt prompt,CancellationToken cancellationToken=default) => OnUi(()=>
    {
        using var form=Dialog(prompt.Title); form.Controls.Add(new Label { Dock=DockStyle.Fill,Text=prompt.Message,Padding=new Padding(14) });
        Buttons(form,new Button { Text=prompt.Action,DialogResult=DialogResult.OK,AutoSize=true }); return Show(form,cancellationToken)==DialogResult.OK;
    },cancellationToken);
    public Task ShowSecretOnceAsync(string title, Secret value, string note, CancellationToken cancellationToken=default) => OnUi(()=>
    {
        using var form=Dialog(title); form.Height=390;
        var field=new TextBox { Dock=DockStyle.Fill,Text=value.Reveal(),ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical };
        var close=new Button { Text="Close",DialogResult=DialogResult.OK,AutoSize=true };
        var copy=new Button { Text="Copy to clipboard",AutoSize=true };
        var bar=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=45,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(6) };
        bar.Controls.Add(close); bar.Controls.Add(copy);
        copy.Click+=(_,_)=> { try { Clipboard.SetText(field.Text); } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(form,"Could not copy: the clipboard is unavailable.",title); } };
        form.Controls.Add(field);
        form.Controls.Add(new Label { Dock=DockStyle.Top,Text=note,Height=90,Padding=new Padding(10) });
        form.Controls.Add(new Label { Dock=DockStyle.Bottom,Text="This is shown once and is not stored anywhere by The Construct.",Height=48,Padding=new Padding(10) });
        form.Controls.Add(bar); form.AcceptButton=close; form.CancelButton=close;
        try { Show(form,cancellationToken); } finally { field.Clear(); }
        return true;
    },cancellationToken);
    public Task<string?> SaveFileAsync(SaveFilePrompt prompt,CancellationToken cancellationToken=default) => OnUi(()=>
    {
        using var dialog=new SaveFileDialog { Title=prompt.Title,FileName=prompt.DefaultPath ?? "",Filter=prompt.Filter ?? "All files|*.*",OverwritePrompt=true };
        return dialog.ShowDialog()==DialogResult.OK ? dialog.FileName : null;
    },cancellationToken);
}
