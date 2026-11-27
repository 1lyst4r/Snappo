using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Snappo.Editor;

namespace Snappo.Overlay;

internal sealed class AnnotationToolbar : Border
{
    private sealed record ToolButtonInfo(EditorTool Tool, string Tooltip, string IconPathData, bool IsFilledIcon = false);

    private static readonly ToolButtonInfo[] ToolButtons =
    {
        new(EditorTool.Draw, "Draw", "M2,11 C3,4 6,4 7,8 S10,13 14,5"),
        new(EditorTool.Arrow, "Arrow", "M3,13 L13,3 M7,3 H13 V9"),
        new(EditorTool.Rectangle, "Rectangle", "M3,4 H13 V12 H3 Z"),
        new(EditorTool.Ellipse, "Circle", "M8,3.5 A5.5,4.5 0 1 1 8,12.5 A5.5,4.5 0 1 1 8,3.5 Z"),
        new(EditorTool.Text, "Text", "M3,4 H13 M8,4 V13"),
        new(EditorTool.Highlight, "Highlight", "M5,11 L11,5 L13,7 L7,13 Z M2.5,14.5 H8"),
        new(EditorTool.Blur, "Blur", "M2,2 H6 V6 H2 Z M10,2 H14 V6 H10 Z M6,6 H10 V10 H6 Z M2,10 H6 V14 H2 Z M10,10 H14 V14 H10 Z", true),
    };

    private const string UndoIconPathData = "M6,3 L2.5,6.5 L6,10 M2.5,6.5 H9.5 C12.5,6.5 13.5,9 13.5,12";
    private const string RedoIconPathData = "M10,3 L13.5,6.5 L10,10 M13.5,6.5 H6.5 C3.5,6.5 2.5,9 2.5,12";

    private static readonly Brush IconBrush = Freeze(new SolidColorBrush(Color.FromRgb(240, 240, 240)));

    private readonly Dictionary<EditorTool, ToggleButton> toolButtons = new();
    private readonly List<(Color Color, ToggleButton Button)> colorSwatches = new();
    private readonly List<(double Size, ToggleButton Button)> textSizeButtons = new();
    private readonly Button undoButton;
    private readonly Button redoButton;
    private readonly ToggleButton fillButton;

    public event Action<EditorTool?>? ToolChanged;

    public event Action? PickPixel;
    public event Action? CopyReport;
    public event Action? UndoClicked;

    public event Action? RedoClicked;

    public event Action<Color>? ColorPicked;

    public event Action<bool>? FillToggled;

    public event Action<double>? TextSizeChanged;

    public AnnotationToolbar()
    {
        Background = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x26, 0x26, 0x26)));
        BorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(3);
        Cursor = Cursors.Arrow;

        var row = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (ToolButtonInfo info in ToolButtons)
        {
            row.Children.Add(CreateToolButton(info));
        }

        fillButton = CreateFillButton();
        row.Children.Add(fillButton);

        foreach (double size in EditorDefaults.TextSizes)
        {
            row.Children.Add(CreateTextSizeButton(size));
        }

        row.Children.Add(CreateSeparator());

        undoButton = CreateActionButton("Undo (Ctrl+Z)", UndoIconPathData, () => UndoClicked?.Invoke());
        redoButton = CreateActionButton("Redo (Ctrl+Y)", RedoIconPathData, () => RedoClicked?.Invoke());
        row.Children.Add(undoButton);
        row.Children.Add(redoButton);

        row.Children.Add(CreateSeparator());

        foreach (Color color in EditorDefaults.Palette)
        {
            row.Children.Add(CreateColorSwatch(color));
        }

        row.Children.Add(CreateActionButton("Pick color and copy HEX (I)", "M3,12 L10,5 L12,7 L5,14 H3 Z M9,4 L13,8", () => PickPixel?.Invoke()));
        row.Children.Add(CreateActionButton("Copy bug report (Ctrl+Shift+C)", "M3,2 H12 V14 H3 Z M5,5 H10 M5,8 H10 M5,11 H8", () => CopyReport?.Invoke()));
        Child = row;

        MouseLeftButtonDown += (_, args) => args.Handled = true;
        MouseRightButtonDown += (_, args) => args.Handled = true;

        SetUndoRedoAvailability(canUndo: false, canRedo: false);
    }

    public void SetUndoRedoAvailability(bool canUndo, bool canRedo)
    {
        undoButton.IsEnabled = canUndo;
        redoButton.IsEnabled = canRedo;
    }

    public void ShowTool(EditorTool? tool)
    {
        foreach ((EditorTool buttonTool, ToggleButton button) in toolButtons)
        {
            button.IsChecked = buttonTool == tool;
        }
    }

    public void ShowColor(Color color)
    {
        foreach ((Color swatchColor, ToggleButton swatchButton) in colorSwatches)
        {
            swatchButton.IsChecked = swatchColor == color;
        }
    }

    public void ShowFill(bool isFilled) => fillButton.IsChecked = isFilled;

    public void ShowTextSize(double size)
    {
        foreach ((double buttonSize, ToggleButton button) in textSizeButtons)
        {
            button.IsChecked = buttonSize == size;
        }
    }

    // Building the buttons
    private ToggleButton CreateToolButton(ToolButtonInfo info)
    {
        var button = new ToggleButton
        {
            Style = FindStyle("ToolbarToggleStyle"),
            ToolTip = info.Tooltip,
            Content = CreateIcon(info.IconPathData, info.IsFilledIcon),
        };

        button.Click += (_, _) =>
        {
            bool toolIsNowOn = button.IsChecked == true;

            foreach (ToggleButton otherButton in toolButtons.Values)
            {
                if (otherButton != button) otherButton.IsChecked = false;
            }

            ToolChanged?.Invoke(toolIsNowOn ? info.Tool : null);
        };

        toolButtons[info.Tool] = button;
        return button;
    }

    private static Button CreateActionButton(string tooltip, string iconPathData, Action onClick)
    {
        var button = new Button
        {
            Style = FindStyle("ToolbarButtonStyle"),
            ToolTip = tooltip,
            Content = CreateIcon(iconPathData, isFilled: false),
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    private ToggleButton CreateColorSwatch(Color color)
    {
        var swatch = new ToggleButton
        {
            Style = FindStyle("ToolbarSwatchStyle"),
            Background = Freeze(new SolidColorBrush(color)),
        };

        swatch.Click += (_, _) =>
        {
            ShowColor(color);          // clicking the selected swatch must not un-select it
            ColorPicked?.Invoke(color);
        };

        colorSwatches.Add((color, swatch));
        return swatch;
    }

    private ToggleButton CreateFillButton()
    {
        var button = new ToggleButton
        {
            Style = FindStyle("ToolbarToggleStyle"),
            ToolTip = "Fill shapes",
            Content = CreateIcon("M3,3 H13 V13 H3 Z", isFilled: true),
        };

        button.Click += (_, _) => FillToggled?.Invoke(button.IsChecked == true);
        return button;
    }

    private ToggleButton CreateTextSizeButton(double size)
    {
        var button = new ToggleButton
        {
            Style = FindStyle("ToolbarToggleStyle"),
            ToolTip = $"Text size {size}",
            Content = new TextBlock
            {
                Text = "A",
                Foreground = IconBrush,
                FontSize = 8 + size / 4,
                FontWeight = FontWeights.Bold,
            },
        };

        button.Click += (_, _) =>
        {
            foreach ((_, ToggleButton otherButton) in textSizeButtons)
            {
                if (otherButton != button) otherButton.IsChecked = false;
            }

            button.IsChecked = true;
            TextSizeChanged?.Invoke(size);
        };

        textSizeButtons.Add((size, button));
        return button;
    }

    private static UIElement CreateIcon(string pathData, bool isFilled)
    {
        var shape = new Path
        {
            Data = Geometry.Parse(pathData),
            Stroke = IconBrush,
            StrokeThickness = isFilled ? 0 : 1.5,
            Fill = isFilled ? IconBrush : null,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };

        return new Canvas { Width = 16, Height = 16, IsHitTestVisible = false, Children = { shape } };
    }

    private static UIElement CreateSeparator() => new System.Windows.Shapes.Rectangle
    {
        Width = 1,
        Height = 18,
        Margin = new Thickness(5, 0, 5, 0),
        Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))),
    };

    private static Style FindStyle(string resourceKey) => (Style)Application.Current.FindResource(resourceKey);

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
