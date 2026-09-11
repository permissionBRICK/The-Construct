using System.Drawing.Drawing2D;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Windows;
namespace Construct.Companion;

// §9.1: a filled disc with a thin ring in the state colour, "?" when unconfigured, a blue dot for an update.
internal static class TrayIconDrawing
{
    public static Icon Draw(TrayAppearance appearance,int dpi)
    {
        var size=TrayModel.IconSize(dpi);
        using var bitmap=new Bitmap(size,size); using var graphics=Graphics.FromImage(bitmap);
        graphics.SmoothingMode=SmoothingMode.AntiAlias;
        var color=appearance.Color switch { TrayColor.Green=>Color.FromArgb(61,211,107),TrayColor.Yellow=>Color.FromArgb(242,195,55),TrayColor.Red=>Color.FromArgb(239,77,77),_=>Color.FromArgb(135,144,154) };
        using var brush=new SolidBrush(color); using var ring=new Pen(Color.FromArgb(220,color),Math.Max(1,size/16f));
        graphics.FillEllipse(brush,3,3,size-6,size-6); graphics.DrawEllipse(ring,1.5f,1.5f,size-3,size-3);
        if (appearance.Question)
        { using var font=new Font(FontFamily.GenericSansSerif,size*0.55f,FontStyle.Bold,GraphicsUnit.Pixel); using var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center }; graphics.DrawString("?",font,Brushes.White,new RectangleF(0,0,size,size),format); }
        if (appearance.Update) { graphics.FillEllipse(Brushes.White,size*0.6f,size*0.6f,size*0.38f,size*0.38f); graphics.FillEllipse(Brushes.DodgerBlue,size*0.65f,size*0.65f,size*0.28f,size*0.28f); }
        var handle=bitmap.GetHicon(); try { using var borrowed=Icon.FromHandle(handle); return (Icon)borrowed.Clone(); } finally { IconHandle.DestroyIcon(handle); }
    }
}
