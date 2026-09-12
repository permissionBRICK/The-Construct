using System.Drawing.Drawing2D;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Windows;
namespace Construct.Companion;

// §9.1: a filled disc with a thin ring in the state colour, "?" when unconfigured, a blue dot for an
// available update and a yellow dot while the installed update still waits for a reprovision.
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
        if (appearance.Mic)
        {
            // A white microphone over the state disc: capsule, cradle arc and stem.
            var w=size*0.26f; var h=size*0.42f; var x=(size-w)/2f; var y=size*0.14f;
            using var capsule=new GraphicsPath(); capsule.AddArc(x,y,w,w,180,180); capsule.AddArc(x,y+h-w,w,w,0,180); capsule.CloseFigure();
            graphics.FillPath(Brushes.White,capsule);
            using var pen=new Pen(Color.White,Math.Max(1,size/12f)) { StartCap=LineCap.Round,EndCap=LineCap.Round };
            graphics.DrawArc(pen,x-size*0.12f,y+h*0.25f,w+size*0.24f,h*0.75f,0,180);
            graphics.DrawLine(pen,size/2f,y+h+size*0.12f,size/2f,size*0.86f);
        }
        if (appearance.Update || appearance.Stale)
        {
            using var dot=new SolidBrush(appearance.Update ? Color.DodgerBlue : Color.FromArgb(242,195,55));
            graphics.FillEllipse(Brushes.White,size*0.6f,size*0.6f,size*0.38f,size*0.38f); graphics.FillEllipse(dot,size*0.65f,size*0.65f,size*0.28f,size*0.28f);
        }
        var handle=bitmap.GetHicon(); try { using var borrowed=Icon.FromHandle(handle); return (Icon)borrowed.Clone(); } finally { IconHandle.DestroyIcon(handle); }
    }
}
