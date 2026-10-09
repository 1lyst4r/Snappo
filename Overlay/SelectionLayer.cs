using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Snappo.Editor;

namespace Snappo.Overlay;

internal sealed class SelectionLayer : FrameworkElement
{
    private const double HandleSize = 8;

    public static readonly Brush DimBrush = CreateFrozenBrush(Color.FromArgb(EditorDefaults.DimAmount, 0, 0, 0));
    private static readonly Brush LabelBackgroundBrush = CreateFrozenBrush(Color.FromArgb(200, 20, 20, 20));
    private static readonly Pen OutlinePen = CreateOutlinePen();
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly Brush HandleBrush = CreateFrozenBrush(Colors.White);
    private static readonly Pen HandleOutlinePen = CreateHandleOutlinePen();

    private Rect? selection;
    private string? sizeLabel;

    public SelectionLayer()
    {
        IsHitTestVisible = false;
    }

    public void Update(Rect? newSelection, string? newSizeLabel = null)
    {
        selection = newSelection;
        sizeLabel = newSizeLabel;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // The dimming itself is a separate static layer; this one only draws the outline, handles and size label.
        if (selection is not Rect selectedArea)
        {
            return;
        }

        double cornerRadius = EditorDefaults.SelectionCornerRadius;

        Rect outlineArea = selectedArea;
        outlineArea.Inflate(0.5, 0.5);
        drawing.DrawRoundedRectangle(null, OutlinePen, outlineArea, cornerRadius + 0.5, cornerRadius + 0.5);

        if (sizeLabel is not null)
        {
            DrawSizeLabel(drawing, selectedArea, sizeLabel);
        }
        else
        {
            DrawHandle(drawing, selectedArea.TopLeft);
            DrawHandle(drawing, selectedArea.TopRight);
            DrawHandle(drawing, selectedArea.BottomLeft);
            DrawHandle(drawing, selectedArea.BottomRight);
        }
    }

    private static void DrawHandle(DrawingContext drawing, Point center)
    {
        var handleRect = new Rect(center.X - HandleSize / 2, center.Y - HandleSize / 2, HandleSize, HandleSize);
        drawing.DrawRectangle(HandleBrush, HandleOutlinePen, handleRect);
    }

    private void DrawSizeLabel(DrawingContext drawing, Rect selectedArea, string labelText)
    {
        const double horizontalPadding = 6;
        const double verticalPadding = 2;
        const double distanceFromSelection = 4;

        var text = new FormattedText(
            labelText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 12,
            Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double boxWidth = text.Width + horizontalPadding * 2;
        double boxHeight = text.Height + verticalPadding * 2;

        double boxTop = selectedArea.Top - boxHeight - distanceFromSelection;
        if (boxTop < 0)
        {
            boxTop = selectedArea.Top + distanceFromSelection;
        }

        var box = new Rect(selectedArea.Left, boxTop, boxWidth, boxHeight);
        drawing.DrawRoundedRectangle(LabelBackgroundBrush, null, box, 4, 4);
        drawing.DrawText(text, new Point(box.Left + horizontalPadding, box.Top + verticalPadding));
    }

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateOutlinePen()
    {
        var pen = new Pen(CreateFrozenBrush(Color.FromArgb(210, 255, 255, 255)), 1);
        pen.Freeze();
        return pen;
    }

    private static Pen CreateHandleOutlinePen()
    {
        var pen = new Pen(CreateFrozenBrush(Color.FromRgb(10, 132, 255)), 1.5);
        pen.Freeze();
        return pen;
    }
}
