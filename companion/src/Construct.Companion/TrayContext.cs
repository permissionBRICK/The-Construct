using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using RemoteHost = Construct.Companion.Core.Remote.RemoteHost;
using Construct.Companion.Core.State;
using Construct.Companion.Windows;
namespace Construct.Companion;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Control dispatcher=new();
    private readonly NotifyIcon tray=new() { Visible=true };
    private readonly ContextMenuStrip menu=new();
    private readonly System.Windows.Forms.Timer clickTimer=new() { Interval=SystemInformation.DoubleClickTime };
    private readonly System.Windows.Forms.Timer refreshTimer=new() { Interval=1000 };
    private readonly Dictionary<string,WebViewWindow> windows=[];
    private readonly IStateFileSystem files;
    private readonly IMessageSink sink;
    private readonly SettingsStore settings;
    private readonly DesktopLauncher launcher;
    private readonly IAudioCapture capture;
    private readonly DesktopRegistration registration;
    private readonly RollingLog log;
    private readonly DesktopSnapshot snapshot;
    private readonly string stateDirectory;
    private readonly string version;
    private readonly IDisposable registryWatch;
    private readonly CancellationTokenSource lifetime=new();
    private string theme="";
    private CancellationTokenSource subscription=new();
    private InstanceRegistry registry;
    private Icon? icon;
    private TrayAppearance? appearance;
    private int iconDpi;
    private bool disposed;
    private readonly PopupGesture popupGesture=new();
    private Rectangle? trayHitArea;
    private bool showOnClick;
    public IPrompts Prompts { get; }
    public TrayContext(IStateFileSystem files,IMessageSink sink,SettingsStore settings,DesktopLauncher launcher,
        DesktopRegistration registration,RollingLog log,IClock clock,IAudioCapture capture,string stateDirectory,string version,ActivationPlan initial)
    {
        this.files=files; this.sink=sink; this.settings=settings; this.launcher=launcher; this.registration=registration;
        this.log=log; this.capture=capture; this.stateDirectory=stateDirectory; this.version=version; snapshot=new(clock);
        _=dispatcher.Handle; Prompts=new DesktopPrompts(dispatcher);
        registry=LoadRegistry();
        tray.ContextMenuStrip=menu; menu.Renderer=new RadioMenuRenderer();
        tray.MouseDown+=(_,e)=>
        {
            var point=Cursor.Position; var dpi=DesktopDisplay.DpiAt(point.X,point.Y); var size=TrayModel.IconSize(dpi);
            trayHitArea=new(point.X-size,point.Y-size,size*2,size*2);
            if (dpi!=iconDpi) { iconDpi=dpi; appearance=null; RefreshIcon(); }
            if (e.Button==MouseButtons.Left) popupGesture.Press(windows.TryGetValue("popup",out var popup) && popup.Visible);
        };
        tray.MouseClick+=(_,e)=> { if (e.Button==MouseButtons.Left) { showOnClick=popupGesture.Click(); clickTimer.Stop(); clickTimer.Start(); } };
        tray.MouseDoubleClick+=(_,e)=> { if (e.Button==MouseButtons.Left) { clickTimer.Stop(); popupGesture.Reset(); HidePopup(); Open("panel",Active); } };
        clickTimer.Tick+=(_,_)=> { clickTimer.Stop(); if (showOnClick) Open("popup",Active); else HidePopup(); };
        menu.Opening+=(_,_)=>BuildMenu();
        refreshTimer.Tick+=(_,_)=>RefreshIcon(); refreshTimer.Start();
        files.CreateDirectory(Path.Combine(new HostState(files).LocalAppData!,"The-Construct"));
        registryWatch=files.Watch(Path.Combine(new HostState(files).LocalAppData!,"The-Construct"),()=>
        { if (!disposed) dispatcher.BeginInvoke(()=> { registry=LoadRegistry(); if (Active is null && registry.ByName.Count > 0 || Active is not null && !registry.ByName.ContainsKey(Active)) Select(settings.Read().ActiveInstance); }); });
        theme=settings.Read().UiTheme;
        settings.Changed+=SettingsChanged;
        Select(settings.Read().ActiveInstance);
        _=ListenCompanionAsync(lifetime.Token);
        dispatcher.BeginInvoke(async ()=> { try { await ApplyPlanAsync(initial); } catch (Exception e) { ShowFailure(e); } });
    }
    private void SettingsChanged(CompanionSettings value)
    {
        if (!disposed) dispatcher.BeginInvoke(()=>
        {
            if (value.ActiveInstance!=Active) Select(value.ActiveInstance);
            if (theme!=value.UiTheme) { theme=value.UiTheme; foreach (var window in windows.Values) window.ReloadTheme(); }
        });
    }
    private async Task ListenCompanionAsync(CancellationToken token)
    {
        try { await foreach (var message in sink.Subscribe("companion",token))
            if (message.GetProperty("type").GetString()=="settings") SettingsChanged(settings.Read()); }
        catch (OperationCanceledException) { }
    }
    private InstanceRegistry LoadRegistry()
    {
        var loaded=InstanceRegistry.Load(files);
        if (loaded.Synthesized && new HostState(files).ResolveScriptsDirectory(overrideDirectory:settings.Read().ScriptsDir) is null) loaded.ByName.Clear();
        return loaded;
    }
    private string? Active=>snapshot.State.Instance;
    private string[] Hosts()=>registry.List().Select(i=>StateJson.Text(i["service"]?["url"])).Where(u=>u is not null).Concat((StateJson.ReadObject(files,Path.Combine(stateDirectory,"hosts.json"))?["hosts"] as JsonArray ?? []).Select(h=>StateJson.Text(h?["url"])).Where(u=>u is not null)).Select(u=>RemoteHost.HostSlug(u!)).Distinct(StringComparer.Ordinal).ToArray();
    private void Select(string? requested)
    {
        if (requested is not null && !registry.ByName.ContainsKey(requested)) requested=null;
        var name=requested ?? registry.List().Select(i=>StateJson.Text(i["name"])).FirstOrDefault();
        subscription.Cancel(); subscription.Dispose(); subscription=new();
        var scripts=name is not null ? new HostState(files).ResolveScriptsDirectory(StateJson.Text(registry.ByName[name]["scriptsDir"]),settings.Read().ScriptsDir) : null;
        snapshot.Select(name,scripts is not null);
        if (settings.Read().ActiveInstance!=name) settings.Update(new JsonObject { ["activeInstance"]=name });
        if (windows.TryGetValue("popup",out var popup)) _=popup.ChangeScopeAsync(name ?? "");
        if (name is not null) _=ListenAsync(name,subscription.Token);
        RefreshIcon();
    }
    private async Task ListenAsync(string name,CancellationToken cancellationToken)
    {
        try
        {
            await using var messages=sink.Subscribe(name,cancellationToken).GetAsyncEnumerator(cancellationToken);
            var next=messages.MoveNextAsync();
            await sink.PostAsync(name,JsonSerializer.SerializeToElement(new {type="ready"},IpcJson.Options),cancellationToken);
            while (await next)
            {
                if (cancellationToken.IsCancellationRequested) return;
                snapshot.Apply(messages.Current); RefreshIcon(); next=messages.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.Write(DesktopLogEvent.BridgeFailed,e); }
    }
    private void RefreshIcon()
    {
        var current=TrayModel.Appearance(snapshot.Current); var dpi=iconDpi>0 ? iconDpi : dispatcher.DeviceDpi;
        if (appearance==current && dpi==iconDpi) return;
        var next=TrayIconDrawing.Draw(current,dpi); tray.Icon=next; icon?.Dispose(); icon=next; tray.Text=current.Tooltip;
        appearance=current; iconDpi=dpi;
    }
    private void BuildMenu()
    {
        foreach (ToolStripItem item in menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose(); menu.Items.Clear();
        var preferences=settings.Read();
        ToolStripMenuItem Build(MenuEntry model)
        {
            var item=new ToolStripMenuItem(model.Text) { Enabled=model.Enabled,Checked=model.Checked,Tag=model.Id };
            if (model.Children is not null) foreach (var child in model.Children) item.DropDownItems.Add(Build(child));
            else item.Click+=async (_,_)=> { try { await CommandAsync(model.Id); } catch (Exception e) { ShowFailure(e); } };
            return item;
        }
        foreach (var item in TrayModel.Menu(snapshot.Current,registry.List().Select(i=>StateJson.String(i["name"])),snapshot.Forwards,preferences.Notifications,registration.Autostart)) menu.Items.Add(Build(item));
    }
    private void ShowFailure(Exception e)
    { log.Write(DesktopLogEvent.ActivationFailed,e); MessageBox.Show("The Companion could not complete this request. See the Companion log for the event code.","Construct Companion",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
    private void Open(string view,string? scope)
    {
        scope ??=view=="hostadmin" ? Hosts().FirstOrDefault() : Active;
        if (view=="hostadmin" && scope is null) { MessageBox.Show("No remote host is registered.","Host Administration"); return; }
        var sinkScope=view=="hostadmin" ? "host:"+scope : scope ?? "";
        var key=view;
        if (!windows.TryGetValue(key,out var window))
        {
            window=new WebViewWindow(view,sinkScope,files,sink,settings,log,launcher,HandleLocalMessageAsync,
                Path.Combine(files.GetRoot(FileSystemRoot.InstallDirectory)!,"media"),Path.Combine(stateDirectory,"webview2"));
            if (view=="popup") window.PopupDeactivated+=()=>popupGesture.FocusLost(
                (Control.MouseButtons & MouseButtons.Left)!=0 && trayHitArea is {} area && area.Contains(Cursor.Position));
            windows.Add(key,window);
        }
        if (view=="popup")
        {
            var screen=trayHitArea is {} hit ? Screen.FromRectangle(hit) : Screen.PrimaryScreen!;
            var work=screen.WorkingArea;
            var point=trayHitArea is {} anchor ? new Point(anchor.X+anchor.Width/2,anchor.Y+anchor.Height/2) : new Point(work.Right-16,work.Bottom+16);
            var size=TrayModel.IconSize(iconDpi>0 ? iconDpi : dispatcher.DeviceDpi);
            var scale=(iconDpi>0 ? iconDpi : dispatcher.DeviceDpi)/96d;
            var bounds=WindowPlacement.Popup(new(point.X-size/2,point.Y-size/2,size,size),new(work.X,work.Y,work.Width,work.Height),(int)(420*scale),(int)(650*scale));
            window.Bounds=new(bounds.X,bounds.Y,bounds.Width,bounds.Height);
        }
        _=window.ChangeScopeAsync(sinkScope); window.Present();
    }
    private void HidePopup() { if (windows.TryGetValue("popup",out var popup)) popup.Hide(); }
    private async Task CommandAsync(string id)
    {
        var preferences=settings.Read();
        if (id.StartsWith("instance:",StringComparison.Ordinal)) { Select(id[9..]); return; }
        switch (id)
        {
            case "panel": case "settings": Open(id,Active); return;
            case "hostadmin": Open(id,snapshot.AdminHost); return;
            case "quit": ExitThread(); return;
            case "notifications": settings.Update(new JsonObject { ["notifications"]=!preferences.Notifications }); return;
            case "autostart": var enabled=!registration.Autostart; registration.SetAutostart(enabled); settings.Update(new JsonObject { ["autostart"]=enabled }); return;
            case "logs": await launcher.OpenAsync(log.PathName); return;
            case "about": MessageBox.Show("Construct Companion\n"+version,"About Construct Companion"); return;
            case "openT3": if (await launcher.OpenT3DesktopAsync()) return; await PostCommandAsync("openAgentWeb",new JsonObject { ["agent"]="t3code" }); return;
        }
        await sink.PostAsync(Active ?? "",DesktopCommand.Message(id,snapshot.State.Mic));
    }

    private Task PostCommandAsync(string id,JsonObject? extra=null,string? scope=null)
    {
        var message=extra ?? new(); message["type"]="command"; message["id"]=id;
        return sink.PostAsync(scope ?? Active ?? "",JsonSerializer.SerializeToElement(message,IpcJson.Options));
    }
    private async Task<bool> HandleLocalMessageAsync(WebViewWindow source,string scope,JsonElement message)
    {
        if (!message.TryGetProperty("type",out var type) || type.ValueKind!=JsonValueKind.String) return false;
        switch (type.GetString())
        {
            case "openPanel": Open("panel",scope); return true;
            case "setInstance":
                var name=message.GetProperty("name").GetString(); if (name is null || !registry.ByName.ContainsKey(name)) throw new ArgumentException("Instance is not registered.");
                Select(name); await source.ChangeScopeAsync(name); return true;
            case "pickTheme":
                var theme=message.GetProperty("id").GetString(); if (theme is not ("classic" or "terminal" or "native")) throw new ArgumentException("Unknown design.");
                settings.Update(new JsonObject { ["uiTheme"]=theme }); foreach (var window in windows.Values) window.ReloadTheme(); return true;
            case "command":
                var id=message.GetProperty("id").GetString();
                if (id=="chooseMicDevice")
                {
                    var devices=await capture.EnumerateDevicesAsync();
                    var selected=await Prompts.PickAsync(new PickPrompt("Microphone device",[new("","System default"),.. devices.Select(d=>new PickItem(d.Id,d.Name,d.IsDefault ? "Default input" : null,settings.Read().MicDevice==d.Id))]));
                    if (selected?.FirstOrDefault() is {} device) settings.Update(new JsonObject { ["micDevice"]=device });
                    return true;
                }
                if (id=="chooseTheme") { Open("theme",scope); return true; }
                if (id=="showLogs") { await launcher.OpenAsync(log.PathName); return true; }
                if (id=="openHostAdmin") { Open("hostadmin",HostForScope(scope)); return true; }
                break;
        }
        return false;
    }
    private string? HostForScope(string scope)=>registry.ByName.TryGetValue(scope,out var instance) && StateJson.Text(instance["service"]?["url"]) is {} url ? RemoteHost.HostSlug(url) : null;
    public async Task ApplyPlanAsync(ActivationPlan plan)
    {
        foreach (var view in plan.Views) Open(view.View,view.View=="hostadmin" ? view.Host : view.Instance);
        if (plan.ForwardId is not null) await PostCommandAsync("openForward",new JsonObject { ["forward"]=plan.ForwardId },plan.ForwardInstance);
    }
    public Task ActivateAsync(IReadOnlyList<UiActivation> activations,CancellationToken cancellationToken=default) => dispatcher.InvokeAsync(()=>
    {
        foreach (var activation in activations)
        {
            if (activation.View=="theme") { Open("theme",activation.Instance); continue; }
            var command=new CommandLine(Panel:activation.View=="panel",Settings:activation.View=="settings",HostAdmin:activation.View=="hostadmin",Popup:activation.View=="popup",Instance:activation.Instance,Host:activation.Host);
            var plan=Activation.Resolve(command,registry.ByName.Keys.ToArray(),Hosts());
            foreach (var view in plan.Views) Open(view.View,view.View=="hostadmin" ? view.Host : view.Instance);
        }
    },cancellationToken);
    public Task QuitAsync(CancellationToken cancellationToken=default) => dispatcher.InvokeAsync(ExitThread,cancellationToken);
    protected override void ExitThreadCore()
    {
        if (!disposed) { subscription.Cancel(); foreach (var window in windows.Values) window.Shutdown(); tray.Visible=false; }
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed=true; settings.Changed-=SettingsChanged; lifetime.Cancel(); lifetime.Dispose(); registryWatch.Dispose(); subscription.Cancel(); subscription.Dispose(); refreshTimer.Dispose(); clickTimer.Dispose();
            foreach (var window in windows.Values) window.Dispose(); menu.Dispose(); tray.Dispose(); icon?.Dispose(); dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
