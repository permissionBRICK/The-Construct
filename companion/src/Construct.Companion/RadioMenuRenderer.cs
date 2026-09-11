namespace Construct.Companion;

internal sealed class RadioMenuRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item.Tag is string id && id.StartsWith("instance:",StringComparison.Ordinal))
        {
            var rect=e.ImageRectangle; var diameter=Math.Max(4,rect.Width/2);
            using var brush=new SolidBrush(e.Item.ForeColor);
            e.Graphics.FillEllipse(brush,rect.X+(rect.Width-diameter)/2,rect.Y+(rect.Height-diameter)/2,diameter,diameter);
        }
        else base.OnRenderItemCheck(e);
    }
}
