using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
namespace Construct.Companion;

internal sealed class WebViewWindow : Form
{
    private readonly WebView2 web = new() { Dock=DockStyle.Fill };
    private readonly IFileSystem files;
    private readonly IMessageSink sink;
    private readonly SettingsStore settings;
    private readonly RollingLog log;
    private readonly Func<WebViewWindow,string,JsonElement,Task<bool>> localMessage;
    private readonly ILauncher launcher;
    private readonly string view;
    private readonly string mediaDirectory;
    private readonly string cacheDirectory;
    private readonly string boundsKey;
    private CancellationTokenSource subscription = new();
    private string scope;
    private string document = "";
    private bool initialized;
    private bool ready;
    private bool exiting;
    private readonly List<JsonElement> pending=[];
    public WebViewWindow(string view,string scope,IFileSystem files,IMessageSink sink,SettingsStore settings,RollingLog log,
        ILauncher launcher,Func<WebViewWindow,string,JsonElement,Task<bool>> localMessage,string mediaDirectory,string cacheDirectory)
    {
        this.view=view; this.scope=scope; this.files=files; this.sink=sink; this.settings=settings; this.log=log;
        this.launcher=launcher; this.localMessage=localMessage; this.mediaDirectory=mediaDirectory; this.cacheDirectory=cacheDirectory;
        boundsKey=view;
        Text=view switch { "settings"=>"Construct Settings", "hostadmin"=>"Host Administration", "theme"=>"Choose Construct Design", _=>"Construct Companion" };
        AutoScaleMode=AutoScaleMode.Dpi; Size=new(1080,800); MinimumSize=new(360,300); StartPosition=FormStartPosition.Manual;
        if (view == "popup") { FormBorderStyle=FormBorderStyle.None; MinimumSize=Size.Empty; TopMost=true; ShowInTaskbar=false; Size=new(420,650); }
        else
        {
            var saved=settings.Bounds(boundsKey) ?? new WindowBounds(100,100,1080,800);
            var work=Screen.FromRectangle(new(saved.X,saved.Y,saved.Width,saved.Height)).WorkingArea;
            var bounds=WindowPlacement.Clamp(saved,new(work.X,work.Y,work.Width,work.Height)); Bounds=new(bounds.X,bounds.Y,bounds.Width,bounds.Height);
        }
        Controls.Add(web);
        if (view=="settings")
        {
            var menu=new MenuStrip();
            foreach (var (label,id) in new[] { ("Choose design…","chooseTheme"),("Microphone device…","chooseMicDevice") })
            { var item=new ToolStripMenuItem(label); item.Click+=async (_,_)=>await localMessage(this,this.scope,JsonSerializer.SerializeToElement(new {type="command",id})); menu.Items.Add(item); }
            Controls.Add(menu); MainMenuStrip=menu;
        }
        Shown += async (_,_) => { if (!initialized) { initialized=true; await InitializeAsync(); } else await OpenRequestedViewAsync(); };
        Deactivate += (_,_) => { if (view == "popup") Hide(); };
        FormClosing += (_,e) => { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel=true; SaveBounds(); Hide(); } };
        ResizeEnd += (_,_)=>SaveBounds();
    }
    private void SaveBounds()
    {
        if (view == "popup" || WindowState != FormWindowState.Normal) return;
        try { settings.SaveBounds(boundsKey,new(Left,Top,Width,Height)); }
        catch (Exception e) { log.Write(DesktopLogEvent.SettingsFailed,e); }
    }
    public void Present()
    {
        if (WindowState==FormWindowState.Minimized) WindowState=FormWindowState.Normal;
        Show(); Activate(); if (ready) _=OpenRequestedViewAsync();
    }
    public async Task ChangeScopeAsync(string value)
    {
        if (scope == value) return;
        subscription.Cancel(); subscription.Dispose(); subscription=new(); scope=value; pending.Clear();
        if (web.CoreWebView2 is not null) { _=ListenAsync(subscription.Token); await sink.PostAsync(scope,JsonSerializer.SerializeToElement(new {type=view=="hostadmin" ? "hostadmin.ready" : "ready"},IpcJson.Options)); }
    }
    private async Task InitializeAsync()
    {
        try
        {
            var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:cacheDirectory);
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.AreDevToolsEnabled=false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;
            web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("construct.media",mediaDirectory,CoreWebView2HostResourceAccessKind.DenyCors);
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WebViewDocument.BridgeScript);
            var palette=JsonSerializer.Serialize(new Dictionary<string,string>
            {
                ["--vscode-editor-background"]=ColorTranslator.ToHtml(SystemColors.Window),
                ["--vscode-editor-foreground"]=ColorTranslator.ToHtml(SystemColors.WindowText),
                ["--vscode-foreground"]=ColorTranslator.ToHtml(SystemColors.WindowText),
                ["--vscode-descriptionForeground"]=ColorTranslator.ToHtml(SystemColors.GrayText),
                ["--vscode-button-background"]=ColorTranslator.ToHtml(SystemColors.Highlight),
                ["--vscode-button-foreground"]=ColorTranslator.ToHtml(SystemColors.HighlightText),
                ["--vscode-input-background"]=ColorTranslator.ToHtml(SystemColors.Window),
                ["--vscode-input-foreground"]=ColorTranslator.ToHtml(SystemColors.WindowText),
                ["--vscode-editorWidget-background"]=ColorTranslator.ToHtml(SystemColors.Control),
                ["--vscode-widget-border"]=ColorTranslator.ToHtml(SystemColors.ControlDark)
            });
            await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('DOMContentLoaded',()=>{for(const [k,v] of Object.entries("+palette+")) document.documentElement.style.setProperty(k,v);});");
            web.CoreWebView2.AddWebResourceRequestedFilter(WebViewDocument.Origin+"/__companion/*",CoreWebView2WebResourceContext.Document);
            web.CoreWebView2.WebResourceRequested += (_,e) =>
            {
                if (e.Request.Uri != DocumentUrl) return;
                e.Response=environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(document)),200,"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
            web.CoreWebView2.NavigationStarting += async (_,e) =>
            {
                if (e.Uri==DocumentUrl) return;
                e.Cancel=true;
                if (Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri) && uri.Scheme is ("https" or "http") && uri.Host!="construct.media")
                    try { await launcher.OpenAsync(uri.AbsoluteUri); } catch (Exception ex) { log.Write(DesktopLogEvent.ActivationFailed,ex); }
            };
            web.CoreWebView2.NewWindowRequested += async (_,e) =>
            {
                e.Handled=true;
                if (Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri) && uri.Scheme is ("https" or "http") && uri.Host!="construct.media")
                    try { await launcher.OpenAsync(uri.AbsoluteUri); } catch (Exception ex) { log.Write(DesktopLogEvent.ActivationFailed,ex); }
            };
            web.CoreWebView2.PermissionRequested += (_,e)=>e.State=CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived += async (_,e) =>
            {
                if (e.Source != DocumentUrl) return;
                try
                {
                    using var message=JsonDocument.Parse(e.WebMessageAsJson);
                    if (message.RootElement.ValueKind != JsonValueKind.Object) return;
                    if (!await localMessage(this,scope,message.RootElement.Clone())) await sink.PostAsync(scope,message.RootElement.Clone(),subscription.Token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    log.Write(DesktopLogEvent.BridgeFailed,ex);
                    web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type="lifecyclePrepared",ok=false,error="The Companion could not complete this request." },IpcJson.Options));
                }
            };
            web.CoreWebView2.NavigationCompleted += async (_,e) =>
            {
                ready=e.IsSuccess;
                if (!ready) return;
                foreach (var message in pending) web.CoreWebView2.PostWebMessageAsJson(message.GetRawText()); pending.Clear();
                await OpenRequestedViewAsync();
            };
            _=ListenAsync(subscription.Token); ReloadTheme();
        }
        catch (Exception e)
        {
            log.Write(DesktopLogEvent.WindowFailed,e);
            Controls.Clear(); Controls.Add(new Label { Dock=DockStyle.Fill,Text="The Construct window could not start. Check that Microsoft Edge WebView2 Runtime is installed.",Padding=new Padding(20) });
            web.Dispose();
        }
    }
    private string DocumentUrl => WebViewDocument.Origin+"/__companion/"+view+".html";
    public void ReloadTheme()
    {
        if (web.CoreWebView2 is null || web.IsDisposed) return;
        var nonce=Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        string Read(string name)=>Encoding.UTF8.GetString(files.ReadFile(Path.Combine(mediaDirectory,name)) ?? throw new FileNotFoundException("Bundled media is missing."));
        var surface=view switch { "popup"=>"launcher", "hostadmin"=>"hostadmin", _=>"panel" };
        if (view == "theme")
        {
            var cards=JsonSerializer.Deserialize<ThemeCard[]>(Read("theme-cards.json"),IpcJson.Options)!;
            document=WebViewDocument.ThemePicker(Read("theme-picker.html"),nonce,cards.Select(c=>c with { PreviewUri=WebViewDocument.Origin+"/theme-previews/"+c.Id+".png" }));
        }
        else document=WebViewDocument.Render(Read(surface+".html"),surface+".js",settings.Read().UiTheme,nonce);
        ready=false; pending.Clear(); web.CoreWebView2.Navigate(DocumentUrl);
    }
    private Task<string> OpenRequestedViewAsync() => ready && view == "settings" ? web.CoreWebView2.ExecuteScriptAsync(WebViewDocument.OpenSettingsScript) : Task.FromResult("");
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in sink.Subscribe(scope,cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested || IsDisposed) return;
                if (ready) web.CoreWebView2.PostWebMessageAsJson(message.GetRawText());
                else if (pending.Count<128) pending.Add(message.Clone());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.Write(DesktopLogEvent.BridgeFailed,e); }
    }
    public void Shutdown() { exiting=true; SaveBounds(); subscription.Cancel(); Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { subscription.Cancel(); subscription.Dispose(); web.Dispose(); }
        base.Dispose(disposing);
    }
}
