using System.Windows;
using System.Windows.Media;

namespace Snappo.Editor.Annotations;

internal sealed class RectangleAnnotation : TwoPointAnnotation
{
    private readonly Brush? fill;
    private readonly Pen? pen;

    public RectangleAnnotation(Point startPoint, Color color, double thickness, bool filled) : base(startPoint)
    {
        fill = filled ? DrawingTools.CreateBrush(color) : null;
        pen = filled ? null : DrawingTools.CreatePen(color, thickness);
    }

    public override void Draw(DrawingContext drawing, RenderContext context)
    {
        drawing.DrawRectangle(fill, pen, EffectiveBounds(context));
    }
}

internal sealed class EllipseAnnotation : TwoPointAnnotation
{
    private readonly Brush? fill;
    private readonly Pen? pen;

    public EllipseAnnotation(Point startPoint, Color color, double thickness, bool filled) : base(startPoint)
    {
        fill = filled ? DrawingTools.CreateBrush(color) : null;
        pen = filled ? null : DrawingTools.CreatePen(color, thickness);
    }

    public override void Draw(DrawingContext drawing, RenderContext context)
    {
        Rect area = EffectiveBounds(context);
        Point center = new(area.X + area.Width / 2, area.Y + area.Height / 2);
        drawing.DrawEllipse(fill, pen, center, area.Width / 2, area.Height / 2);
    }
}

internal sealed class BlurAnnotation : TwoPointAnnotation
{
    public BlurAnnotation(Point startPoint) : base(startPoint)
    {
    }

    protected override bool SupportsShiftConstraint => false;

    public override void Draw(DrawingContext drawing, RenderContext context)
    {
        Rect area = EffectiveBounds(context);
        if (area.Width < 1 || area.Height < 1)
        {
            return;
        }

        drawing.PushClip(new RectangleGeometry(area));
        context.PixelatedScreenshot.Value.Draw(drawing, context.ScreenBounds);
        drawing.Pop();
    }
}
