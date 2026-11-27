using System;
using System.Windows;
using System.Windows.Media;

namespace Snappo.Editor.Annotations;

internal abstract class Annotation
{
    public abstract void Draw(DrawingContext drawing, RenderContext context);
}

internal abstract class DragAnnotation : Annotation
{
    protected DragAnnotation(Point startPoint)
    {
        StartPoint = startPoint;
    }

    public Point StartPoint { get; }

    public abstract void UpdateDrag(Point currentPoint);

    public virtual bool IsWorthKeeping => true;
}

internal abstract class TwoPointAnnotation : DragAnnotation
{
    protected TwoPointAnnotation(Point startPoint) : base(startPoint)
    {
        EndPoint = startPoint;
    }

    public Point EndPoint { get; private set; }

    private bool isConstrained;

    public bool IsConstrained
    {
        get => isConstrained;
        set => isConstrained = value && SupportsShiftConstraint;
    }

    protected virtual bool SupportsShiftConstraint => true;

    public override void UpdateDrag(Point currentPoint)
    {
        EndPoint = currentPoint;
    }

    public override bool IsWorthKeeping => (EndPoint - StartPoint).Length >= EditorDefaults.MinimumShapeSize;

    public void CommitConstraint(Rect selection)
    {
        EndPoint = ConstrainedEndPoint(selection);
        IsConstrained = false;
    }

    protected Point ConstrainedEndPoint(Rect selection)
    {
        if (!IsConstrained)
        {
            return EndPoint;
        }

        Point constrained = ApplyConstraint(StartPoint, EndPoint, selection);

        return new Point(
            Math.Clamp(constrained.X, selection.Left, selection.Right),
            Math.Clamp(constrained.Y, selection.Top, selection.Bottom));
    }

    protected Rect EffectiveBounds(RenderContext context) => new(StartPoint, ConstrainedEndPoint(context.SelectionBounds));

    protected virtual Point ApplyConstraint(Point startPoint, Point rawEndPoint, Rect selection)
    {
        double deltaX = rawEndPoint.X - startPoint.X;
        double deltaY = rawEndPoint.Y - startPoint.Y;
        double directionX = deltaX < 0 ? -1 : 1;
        double directionY = deltaY < 0 ? -1 : 1;

        double roomX = directionX > 0 ? selection.Right - startPoint.X : startPoint.X - selection.Left;
        double roomY = directionY > 0 ? selection.Bottom - startPoint.Y : startPoint.Y - selection.Top;

        double side = Math.Min(Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)), Math.Min(roomX, roomY));
        side = Math.Max(0, side);

        return new Point(startPoint.X + side * directionX, startPoint.Y + side * directionY);
    }
}
