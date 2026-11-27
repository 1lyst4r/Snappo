using System;
using System.Windows;
using System.Windows.Media;

namespace Snappo.Editor.Annotations;

internal sealed class ArrowAnnotation : TwoPointAnnotation
{
    private readonly Pen linePen;
    private readonly Pen headOutlinePen;
    private readonly Brush headBrush;
    private readonly double thickness;

    public ArrowAnnotation(Point startPoint, Color color, double thickness) : base(startPoint)
    {
        this.thickness = thickness;
        linePen = DrawingTools.CreatePen(color, thickness);
        headOutlinePen = DrawingTools.CreatePen(color, 1);   // thin rounded outline softens the arrowhead corners
        headBrush = DrawingTools.CreateBrush(color);
    }

    public override void Draw(DrawingContext drawing, RenderContext context)
    {
        Point tip = ConstrainedEndPoint(context.SelectionBounds);

        Vector direction = tip - StartPoint;
        double length = direction.Length;
        if (length < 1)
        {
            return;
        }

        direction /= length;   // now a unit vector pointing from the tail to the tip

        double headLength = Math.Min(length, Math.Max(thickness * 5, 14));
        double headHalfWidth = headLength * 0.4;

        Vector sideways = new(-direction.Y, direction.X);
        Point headBase = tip - direction * headLength;

        drawing.DrawLine(linePen, StartPoint, headBase + direction);   // +1 unit overlap hides any seam

        var headShape = new StreamGeometry();
        using (StreamGeometryContext figure = headShape.Open())
        {
            figure.BeginFigure(tip, true, true);
            figure.LineTo(headBase + sideways * headHalfWidth, true, false);
            figure.LineTo(headBase - sideways * headHalfWidth, true, false);
        }

        headShape.Freeze();
        drawing.DrawGeometry(headBrush, headOutlinePen, headShape);
    }

    protected override Point ApplyConstraint(Point startPoint, Point rawEndPoint, Rect selection)
    {
        Vector raw = rawEndPoint - startPoint;
        double length = raw.Length;
        if (length < 1)
        {
            return rawEndPoint;
        }

        const double step = Math.PI / 4;   // 45 degrees
        double snappedAngle = Math.Round(Math.Atan2(raw.Y, raw.X) / step) * step;
        Vector direction = new(Math.Cos(snappedAngle), Math.Sin(snappedAngle));

        double roomX = direction.X > 1e-9 ? (selection.Right - startPoint.X) / direction.X
                     : direction.X < -1e-9 ? (startPoint.X - selection.Left) / -direction.X
                     : double.PositiveInfinity;
        double roomY = direction.Y > 1e-9 ? (selection.Bottom - startPoint.Y) / direction.Y
                     : direction.Y < -1e-9 ? (startPoint.Y - selection.Top) / -direction.Y
                     : double.PositiveInfinity;

        double fittingLength = Math.Max(0, Math.Min(length, Math.Min(roomX, roomY)));
        return startPoint + direction * fittingLength;
    }
}
