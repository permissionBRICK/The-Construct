namespace Construct.Companion.Core.Desktop;

public enum TrayColor { Green, Yellow, Grey, Red }
public sealed record TrayState(string? Instance = null, bool ScriptsFound = false, bool Online = false,
    string VmState = "unknown", bool Busy = false, bool ProbeError = false, TimeSpan UnknownFor = default,
    int ForwardCount = 0, bool Mic = false, bool UpdateAvailable = false, bool HostAdmin = false);
public sealed record TrayAppearance(TrayColor Color, bool Question, bool Update, string Tooltip);
public sealed record MenuEntry(string Id, string Text, bool Enabled = true, bool Checked = false,
    IReadOnlyList<MenuEntry>? Children = null);
public sealed record TrayForward(string Id, string Label, string Url);
public static class TrayModel
{
    public static TrayAppearance Appearance(TrayState state)
    {
        var missing = state.Instance is null || !state.ScriptsFound;
        var color = missing ? TrayColor.Grey : state.ProbeError ? TrayColor.Red : state.Busy ? TrayColor.Yellow : state.Online ? TrayColor.Green :
            state.VmState == "absent" || (state.VmState == "unknown" && state.UnknownFor > TimeSpan.FromMinutes(2)) ? TrayColor.Red :
            state.VmState is "off" or "saved" or "paused" ? TrayColor.Grey : TrayColor.Yellow;
        var status = missing ? "not configured" : state.Busy ? "working" : state.Online ? "online" : state.VmState;
        var tooltip = $"{state.Instance ?? "Construct"} · {status} · {state.ForwardCount} forwards · mic {(state.Mic ? "on" : "off")}";
        return new(color, missing, state.UpdateAvailable, tooltip.Length <= 63 ? tooltip : tooltip[..62] + "…");
    }
    public static int IconSize(int dpi) => dpi <= 96 ? 16 : dpi <= 120 ? 20 : dpi <= 144 ? 24 : 32;
    public static IReadOnlyList<MenuEntry> Menu(TrayState state, IEnumerable<string> instances,
        IReadOnlyList<TrayForward> forwards, bool notifications, bool autostart)
    {
        var usable = state.Instance is not null;
        var power = state.Online ? "shutdown" : "startVm";
        var entries = new List<MenuEntry>
        {
            new("instances", "Instance", Children: instances.Select(n => new MenuEntry("instance:" + n, n, Checked: n == state.Instance)).ToArray()),
            new("status", Appearance(state).Tooltip, false),
            new(power, state.Online ? "Shutdown" : state.VmState is "saved" or "paused" ? "Resume" : "Start", usable && !state.Busy && (state.Online || state.VmState is not ("absent" or "running"))),
            new("connect", "Open VS Code", usable), new("openT3", "Open T3 Code", usable),
            new("forwards", "Forwards", Children: forwards.Count == 0 ? [new("none", "none", false)] : forwards.Select(f => new MenuEntry("forward:" + f.Id, f.Label, Children: [new("openForward:" + f.Id, "Open link"), new("closeForward:" + f.Id, "Close")])).ToArray()),
            new("mic", "Microphone passthrough", usable, state.Mic), new("notifications", "Notifications", Checked: notifications),
            new("panel", "Control Panel"), new("settings", "Settings")
        };
        if (state.HostAdmin) entries.Add(new("hostadmin", "Host Administration"));
        entries.AddRange([new("autostart", "Start with Windows", Checked: autostart), new("logs", "Logs"), new("about", "About"), new("quit", "Quit")]);
        return entries;
    }
}
public sealed record WindowBounds(int X, int Y, int Width, int Height);
public static class WindowPlacement
{
    public static WindowBounds Clamp(WindowBounds desired, WindowBounds work)
    {
        var width = Math.Min(Math.Max(1, desired.Width), work.Width); var height = Math.Min(Math.Max(1, desired.Height), work.Height);
        return new(Math.Clamp(desired.X, work.X, work.X + work.Width - width), Math.Clamp(desired.Y, work.Y, work.Y + work.Height - height), width, height);
    }
    public static WindowBounds Popup(WindowBounds tray, WindowBounds work, int width, int height)
    {
        var x = tray.X + tray.Width - width; var y = tray.Y - height;
        if (tray.Y <= work.Y) y = tray.Y + tray.Height;
        if (tray.X <= work.X) x = tray.X + tray.Width;
        return Clamp(new(x, y, width, height), work);
    }
}
