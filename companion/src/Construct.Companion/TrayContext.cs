using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Windows;
using RemoteHost = Construct.Companion.Core.Remote.RemoteHost;
namespace Construct.Companion;

// Tray icon, popup gesture, context menu and the window registry. Every decision lives in
// Core (TrayModel, DesktopSnapshot, Activation, DesktopCommand); this class only renders and forwards.
internal sealed class TrayContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray = new() { Visible = true };
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer clickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1000 };
    private readonly Dictionary<string, WebViewWindow> windows = [];
    private readonly Platform platform;
    private readonly IMessageSink sink;
    private readonly IpcSettings settings;
    private readonly IPrompts prompts;
    private readonly DesktopSnapshot snapshot;
    private readonly IDisposable registryWatch;
    private readonly CancellationTokenSource lifetime = new();
    private readonly PopupGesture popupGesture = new();
    private CancellationTokenSource subscription = new();
    private InstanceRegistry registry;
    private string theme;
    private Icon? icon;
    private TrayAppearance? appearance;
    private int iconDpi;
    private bool disposed;
    private Rectangle? trayHitArea;
    private bool showOnClick;
    public TrayContext(Platform platform, IMessageSink sink, IpcSettings settings, IPrompts prompts, ActivationPlan initial)
    {
        this.platform = platform; this.sink = sink; this.settings = settings; this.prompts = prompts;
        snapshot = new(platform.Clock);
        _ = dispatcher.Handle;
        registry = LoadRegistry();
        theme = settings.Read().UiTheme;
        tray.ContextMenuStrip = menu; menu.Renderer = new RadioMenuRenderer();
        tray.MouseDown += (_, e) =>
        {
            var point = Cursor.Position; var dpi = DisplayDpi.At(point.X, point.Y); var size = TrayModel.IconSize(dpi);
            trayHitArea = new(point.X - size, point.Y - size, size * 2, size * 2);
            if (dpi != iconDpi) { iconDpi = dpi; appearance = null; RefreshIcon(); }
            if (e.Button == MouseButtons.Left) popupGesture.Press(windows.TryGetValue("popup", out var popup) && popup.Visible);
        };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) { showOnClick = popupGesture.Click(); clickTimer.Stop(); clickTimer.Start(); } };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) { clickTimer.Stop(); popupGesture.Reset(); HidePopup(); Open("panel", Active); } };
        clickTimer.Tick += (_, _) => { clickTimer.Stop(); if (showOnClick) Open("popup", Active); else HidePopup(); };
        menu.Opening += (_, _) => BuildMenu();
        refreshTimer.Tick += (_, _) => RefreshIcon(); refreshTimer.Start();
        platform.Files.CreateDirectory(platform.StateRoot);
        registryWatch = platform.Files.Watch(platform.StateRoot, () => { if (!disposed) dispatcher.BeginInvoke(RegistryChanged); });
        settings.Changed += SettingsChanged;
        Select(settings.Read().ActiveInstance);
        _ = ListenCompanionAsync(lifetime.Token);
        dispatcher.BeginInvoke(async () => { try { await OpenPlanAsync(initial); } catch (Exception e) { ShowFailure(e); } }); // startup must never kill the tray
    }
    private string? Active => snapshot.State.Instance;
    private InstanceRegistry LoadRegistry() => InstanceRegistry.LoadUsable(platform.Files, settings.Read().ScriptsDir);
    private string[] Hosts() => RemoteHost.KnownHostSlugs(registry, RemoteHost.EnrolledHosts(platform.Files, platform.StateDirectory));
    private void RegistryChanged()
    {
        registry = LoadRegistry();
        var selectionStale = Active is null ? registry.ByName.Count > 0 : !registry.ByName.ContainsKey(Active);
        if (selectionStale) Select(settings.Read().ActiveInstance);
    }
    private void SettingsChanged(CompanionSettings value)
    {
        if (!disposed) dispatcher.BeginInvoke(() =>
        {
            if (value.ActiveInstance != Active) Select(value.ActiveInstance);
            if (theme != value.UiTheme) { theme = value.UiTheme; foreach (var window in windows.Values) window.ReloadTheme(); }
        });
    }
    private async Task ListenCompanionAsync(CancellationToken token)
    {
        try
        {
            await foreach (var message in sink.Subscribe("companion", token))
                if (message.GetProperty("type").GetString() == "settings") SettingsChanged(settings.Read());
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void Select(string? requested)
    {
        if (requested is not null && !registry.ByName.ContainsKey(requested)) requested = null;
        var name = requested ?? registry.List().Select(i => StateJson.Text(i["name"])).FirstOrDefault();
        subscription.Cancel(); subscription.Dispose(); subscription = new();
        var scripts = name is not null ? new HostState(platform.Files).ResolveScriptsDirectory(StateJson.Text(registry.ByName[name]["scriptsDir"]), settings.Read().ScriptsDir) : null;
        snapshot.Select(name, scripts is not null);
        if (settings.Read().ActiveInstance != name) settings.Merge(new JsonObject { ["activeInstance"] = name });
        if (windows.TryGetValue("popup", out var popup)) _ = popup.ChangeScopeAsync(name ?? "");
        if (name is not null) _ = ListenAsync(name, subscription.Token);
        RefreshIcon();
    }
    private async Task ListenAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await using var messages = sink.Subscribe(name, cancellationToken).GetAsyncEnumerator(cancellationToken);
            var next = messages.MoveNextAsync();
            await sink.PostAsync(name, JsonSerializer.SerializeToElement(new { type = "ready" }, IpcJson.Options), cancellationToken);
            while (await next)
            {
                if (cancellationToken.IsCancellationRequested) return;
                snapshot.Apply(messages.Current); RefreshIcon(); next = messages.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { platform.Log.Write(DesktopLogEvent.BridgeFailed, e); } // the subscription loop must outlive a bad message
    }
    private void RefreshIcon()
    {
        var current = TrayModel.Appearance(snapshot.Current); var dpi = iconDpi > 0 ? iconDpi : dispatcher.DeviceDpi;
        if (appearance == current && dpi == iconDpi) return;
        var next = TrayIconDrawing.Draw(current, dpi); tray.Icon = next; icon?.Dispose(); icon = next; tray.Text = current.Tooltip;
        appearance = current; iconDpi = dpi;
    }
    private void BuildMenu()
    {
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        menu.Items.Clear();
        ToolStripMenuItem Build(MenuEntry model)
        {
            var item = new ToolStripMenuItem(model.Text) { Enabled = model.Enabled, Checked = model.Checked, Tag = model.Id };
            if (model.Children is not null) foreach (var child in model.Children) item.DropDownItems.Add(Build(child));
            else item.Click += async (_, _) => { try { await CommandAsync(model.Id); } catch (Exception e) { ShowFailure(e); } };
            return item;
        }
        foreach (var entry in TrayModel.Menu(snapshot.Current, registry.List().Select(i => StateJson.String(i["name"])), snapshot.Forwards, settings.Read().Notifications, platform.Registration.Autostart))
            menu.Items.Add(Build(entry));
    }
    private void ShowFailure(Exception e)
    {
        platform.Log.Write(DesktopLogEvent.ActivationFailed, e);
        MessageBox.Show("The Companion could not complete this request. See the Companion log for the event code.", "Construct Companion", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private void Open(string view, string? scope)
    {
        scope ??= view == "hostadmin" ? Hosts().FirstOrDefault() : Active;
        if (view == "hostadmin" && scope is null) { MessageBox.Show("No remote host is registered.", "Host Administration"); return; }
        var sinkScope = view == "hostadmin" ? "host:" + scope : scope ?? "";
        if (!windows.TryGetValue(view, out var window))
        {
            window = new WebViewWindow(view, sinkScope, platform, sink, settings, HandleLocalMessageAsync);
            if (view == "popup") window.PopupDeactivated += () => popupGesture.FocusLost(
                (Control.MouseButtons & MouseButtons.Left) != 0 && trayHitArea is { } area && area.Contains(Cursor.Position));
            windows.Add(view, window);
        }
        if (view == "popup")
        {
            var screen = trayHitArea is { } hit ? Screen.FromRectangle(hit) : Screen.PrimaryScreen!;
            var work = screen.WorkingArea;
            var point = trayHitArea is { } anchor ? new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2) : new Point(work.Right - 16, work.Bottom + 16);
            var dpi = iconDpi > 0 ? iconDpi : dispatcher.DeviceDpi;
            var size = TrayModel.IconSize(dpi); var (width, height) = TrayModel.PopupSize(dpi);
            var bounds = WindowPlacement.Popup(new(point.X - size / 2, point.Y - size / 2, size, size), new(work.X, work.Y, work.Width, work.Height), width, height);
            window.Bounds = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        _ = window.ChangeScopeAsync(sinkScope); window.Present();
    }
    private void HidePopup() { if (windows.TryGetValue("popup", out var popup)) popup.Hide(); }
    private async Task CommandAsync(string id)
    {
        if (id.StartsWith(TrayModel.InstancePrefix, StringComparison.Ordinal)) { Select(id[TrayModel.InstancePrefix.Length..]); return; }
        switch (id)
        {
            case "panel": case "settings": Open(id, Active); return;
            case "hostadmin": Open(id, snapshot.AdminHost); return;
            case "quit": ExitThread(); return;
            case "notifications": settings.Merge(new JsonObject { ["notifications"] = !settings.Read().Notifications }); return;
            case "autostart":
                // The Run key is the truth; settings.autostart mirrors it so the installer can keep an explicit "off".
                var enabled = !platform.Registration.Autostart; platform.Registration.SetAutostart(enabled); settings.Merge(new JsonObject { ["autostart"] = enabled }); return;
            case "logs": await platform.Launcher.OpenAsync(platform.Log.PathName); return;
            case "about": MessageBox.Show("Construct Companion\n" + platform.Version, "About Construct Companion"); return;
            case "openT3": if (await platform.Launcher.OpenT3DesktopAsync()) return; await PostCommandAsync("openAgentWeb", new JsonObject { ["agent"] = "t3code" }); return;
        }
        await sink.PostAsync(Active ?? "", DesktopCommand.Message(id, snapshot.State.Mic));
    }
    private Task PostCommandAsync(string id, JsonObject? extra = null, string? scope = null)
    {
        var message = extra ?? new(); message["type"] = "command"; message["id"] = id;
        return sink.PostAsync(scope ?? Active ?? "", JsonSerializer.SerializeToElement(message, IpcJson.Options));
    }
    // Messages a window handles locally instead of posting to the dispatcher: view changes and the
    // dialogs that need the window's own UI thread.
    private async Task<bool> HandleLocalMessageAsync(WebViewWindow source, string scope, JsonElement message)
    {
        if (!message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return false;
        switch (type.GetString())
        {
            case "openPanel": Open("panel", scope); return true;
            case "setInstance":
                var name = message.GetProperty("name").GetString(); if (name is null || !registry.ByName.ContainsKey(name)) throw new ArgumentException("Instance is not registered.");
                Select(name); await source.ChangeScopeAsync(name); return true;
            case "pickTheme":
                var picked = message.GetProperty("id").GetString(); if (!WebViewDocument.IsKnownTheme(picked)) throw new ArgumentException("Unknown design.");
                settings.Merge(new JsonObject { ["uiTheme"] = picked }); foreach (var window in windows.Values) window.ReloadTheme(); return true;
            case "command":
                var id = message.GetProperty("id").GetString();
                if (id == "chooseMicDevice")
                {
                    var devices = await platform.Capture.EnumerateDevicesAsync();
                    var selected = await prompts.PickAsync(new PickPrompt("Microphone device", [new("", "System default"), .. devices.Select(d => new PickItem(d.Id, d.Name, d.IsDefault ? "Default input" : null, settings.Read().MicDevice == d.Id))]));
                    if (selected?.FirstOrDefault() is { } device) settings.Merge(new JsonObject { ["micDevice"] = device });
                    return true;
                }
                if (id == "chooseTheme") { Open("theme", scope); return true; }
                if (id == "showLogs") { await platform.Launcher.OpenAsync(platform.Log.PathName); return true; }
                if (id == "openHostAdmin") { Open("hostadmin", HostForScope(scope)); return true; }
                break;
        }
        return false;
    }
    private string? HostForScope(string scope) => registry.ByName.TryGetValue(scope, out var instance) && StateJson.Text(instance["service"]?["url"]) is { } url ? RemoteHost.HostSlug(url) : null;
    private async Task OpenPlanAsync(ActivationPlan plan)
    {
        foreach (var view in plan.Views) Open(view.View, view.View == "hostadmin" ? view.Host : view.Instance);
        if (plan.ForwardId is not null) await PostCommandAsync("openForward", new JsonObject { ["forward"] = plan.ForwardId }, plan.ForwardInstance);
    }
    public Task ActivateAsync(IReadOnlyList<UiActivation> activations, CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(async ct =>
    {
        foreach (var activation in activations) await OpenPlanAsync(Activation.ResolveView(activation, registry.ByName.Keys.ToArray(), Hosts()));
    }, cancellationToken);
    public Task QuitAsync(CancellationToken cancellationToken = default) => dispatcher.InvokeAsync(ExitThread, cancellationToken);
    protected override void ExitThreadCore()
    {
        if (!disposed) { subscription.Cancel(); foreach (var window in windows.Values) window.Shutdown(); tray.Visible = false; }
        base.ExitThreadCore();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true; settings.Changed -= SettingsChanged; lifetime.Cancel(); lifetime.Dispose(); registryWatch.Dispose(); subscription.Cancel(); subscription.Dispose(); refreshTimer.Dispose(); clickTimer.Dispose();
            foreach (var window in windows.Values) window.Dispose(); menu.Dispose(); tray.Dispose(); icon?.Dispose(); dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
