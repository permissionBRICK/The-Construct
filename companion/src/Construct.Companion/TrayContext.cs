using Construct.Companion.Windows;

namespace Construct.Companion;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly Icon icon;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };

    public TrayContext(SingleInstance instance)
    {
        icon = NoInstanceIcon();
        menu.Items.Add("No instance registered").Enabled = false;
        menu.Items.Add("Quit", null, (_, _) => ExitThread());
        tray = new NotifyIcon
        {
            Icon = icon, Text = "No instance · 0 forwards · mic off", ContextMenuStrip = menu, Visible = true
        };
        timer.Tick += (_, _) => { if (instance.QuitRequested()) ExitThread(); };
        timer.Start();
    }

    private static Icon NoInstanceIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        graphics.FillEllipse(Brushes.Gray, 2, 2, 28, 28);
        graphics.DrawEllipse(Pens.Silver, 2, 2, 28, 28);
        using var font = new Font(FontFamily.GenericSansSerif, 17, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("?", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { IconHandle.DestroyIcon(handle); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); tray.Visible = false; tray.Dispose(); icon.Dispose(); menu.Dispose(); }
        base.Dispose(disposing);
    }
}
