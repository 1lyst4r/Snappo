using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace Snappo.Editor.Annotations;

internal sealed class TextAnnotation : Annotation
{
    private static readonly Typeface TextTypeface = new(EditorDefaults.TextFontName);

    private readonly StringBuilder typedText = new();
    private Brush brush;
    private Pen caretPen;

    public TextAnnotation(Point position, Color color, double fontSize)
    {
        Position = position;
        FontSize = fontSize;
        brush = DrawingTools.CreateBrush(color);
        caretPen = DrawingTools.CreatePen(color, 1.5, PenLineCap.Flat);
    }

    public Point Position { get; }
    public double FontSize { get; set; }

    public bool IsEditing { get; set; }

    public bool HasText => typedText.ToString().Trim().Length > 0;

    public void ChangeColor(Color color)
    {
        brush = DrawingTools.CreateBrush(color);
        caretPen = DrawingTools.CreatePen(color, 1.5, PenLineCap.Flat);
    }

    public void Append(string text) => typedText.Append(text);

    public void AppendNewLine() => typedText.Append('\n');

    public void RemoveLastCharacter()
    {
        if (typedText.Length == 0) return;

        int[] starts = StringInfo.ParseCombiningCharacters(typedText.ToString());
        typedText.Length = starts[^1];
    }

    public override void Draw(DrawingContext drawing, RenderContext context)
    {
        string content = typedText.ToString();

        if (content.Length > 0)
        {
            drawing.DrawText(CreateFormattedText(content, context.PixelsPerDip), Position);
        }

        if (IsEditing)
        {
            DrawCaret(drawing, content, context.PixelsPerDip);
        }
    }

    private void DrawCaret(DrawingContext drawing, string content, double pixelsPerDip)
    {
        int lastLineBreak = content.LastIndexOf('\n');
        string lastLine = content.Substring(lastLineBreak + 1);
        int lineIndex = content.Length - content.Replace("\n", string.Empty).Length;   // one line break = one line down

        double lineHeight = CreateFormattedText("Ag", pixelsPerDip).Height;
        double caretLeft = Position.X + (lastLine.Length > 0
            ? CreateFormattedText(lastLine, pixelsPerDip).WidthIncludingTrailingWhitespace
            : 0);
        double caretTop = Position.Y + lineIndex * lineHeight;

        drawing.DrawLine(caretPen, new Point(caretLeft + 1, caretTop), new Point(caretLeft + 1, caretTop + lineHeight));
    }

    private FormattedText CreateFormattedText(string text, double pixelsPerDip) => new(
        text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextTypeface, FontSize, brush, pixelsPerDip);
}
