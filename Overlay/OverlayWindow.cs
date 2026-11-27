using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snappo.Capture;
using Snappo.Editor;
using Snappo.Editor.Annotations;
using Snappo.Interop;
using Snappo.Output;

namespace Snappo.Overlay;

internal sealed class OverlayWindow : Window
{
    private enum Phase
    {
        WaitingForSelection,
        DraggingSelection,
        Annotating,
    }

    private enum ResizeHandle
    {
        None,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private const double ToolbarGap = 8;            // space between selection and toolbar
    private const double ToolbarScreenMargin = 6;   // keep the toolbar this far from the monitor edge
    private const double HandleHitRadius = 10;      // generous hit target around each corner handle

    private static readonly BitmapSource PlaceholderBitmap = CreatePlaceholderBitmap();

    private MonitorShot monitorShot;
    private CaptureSession session;
    private Lazy<PixelatedImage> pixelatedScreenshot;
    private readonly Image backgroundImage;
    private readonly Image selectedAreaImage;       // the same screenshot again, undimmed, clipped to the selection
    private readonly Border dimLayer = new() { Background = SelectionLayer.DimBrush, IsHitTestVisible = false };
    private readonly AnnotationHistory history = new();
    private readonly SelectionLayer selectionLayer = new();
    private readonly AnnotationLayer committedLayer;
    private readonly AnnotationLayer liveLayer;
    private readonly AnnotationToolbar toolbar = new();
    private readonly Canvas toolbarCanvas = new();
    private readonly Grid surface;

    private readonly bool isWarmUp;
    private bool isClosingForGood;

    private IntPtr windowHandle;
    private Phase phase = Phase.WaitingForSelection;
    private Point selectionAnchor;                  // the corner where the drag started
    private Rect selection = Rect.Empty;
    private EditorTool? activeTool = EditorMemory.LastTool;                 // the tool you used last time
    private Color currentColor = EditorMemory.LastColor;
    private double currentTextSize = EditorMemory.LastTextSize;
    private bool shapesAreFilled = EditorMemory.ShapesAreFilled;
    private DragAnnotation? annotationBeingDragged;
    private TextAnnotation? textBeingTyped;
    private Point lastRawMousePosition;             // updated on every mouse move; lets a key event react without one

    // Space-to-move
    private bool spaceHeld;
    private Point spaceMoveStartMouse;
    private Rect spaceMoveStartSelection;

    // Moving/resizing a placed selection
    private ResizeHandle draggedHandle = ResizeHandle.None;
    private Point resizeAnchor;
    private bool isMovingSelection;
    private Point moveDragStartMouse;
    private Rect moveDragStartSelection;

    public OverlayWindow(MonitorShot monitorShot, CaptureSession session, Int32Rect? displayBounds = null, bool isWarmUp = false)
    {
        this.monitorShot = monitorShot;
        MonitorBounds = displayBounds ?? monitorShot.Bounds;
        this.session = session;
        this.isWarmUp = isWarmUp;

        committedLayer = new AnnotationLayer(BuildRenderContext);
        liveLayer = new AnnotationLayer(BuildRenderContext);
        pixelatedScreenshot = new Lazy<PixelatedImage>(CreatePixelatedScreenshot);

        backgroundImage = new Image { Source = monitorShot.Bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(backgroundImage, BitmapScalingMode.NearestNeighbor);   // 1:1 pixels, no smoothing

        selectedAreaImage = new Image { Source = monitorShot.Bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false, Clip = Geometry.Empty };
        RenderOptions.SetBitmapScalingMode(selectedAreaImage, BitmapScalingMode.NearestNeighbor);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        surface = BuildLayout();
        Content = surface;
        committedLayer.Show(history.Applied);
        ShowSelection(null);

        history.Changed += OnHistoryChanged;
        toolbar.ToolChanged += OnToolChanged;
        toolbar.UndoClicked += Undo;
        toolbar.RedoClicked += Redo;
        toolbar.ColorPicked += OnColorPicked;
        toolbar.FillToggled += OnFillToggled;
        toolbar.TextSizeChanged += OnTextSizeChanged;
        toolbar.ShowTool(activeTool);
        toolbar.ShowColor(currentColor);
        toolbar.ShowFill(shapesAreFilled);
        toolbar.ShowTextSize(currentTextSize);
    }

    internal void ShowForCapture(Size? originalSize = null)
    {
        if (originalSize is Size size)
        {
            Content = null;
            surface.Width = size.Width;
            surface.Height = size.Height;
            Content = new Viewbox { Child = surface, Stretch = Stretch.Uniform };
        }

        Show();
        PlaceOnMonitor();
        UpdateLayout();
    }

    // Pooling
    public Int32Rect MonitorBounds { get; }

    internal void RearmForCapture(MonitorShot newShot, CaptureSession newSession)
    {
        monitorShot = newShot;
        session = newSession;
        pixelatedScreenshot = new Lazy<PixelatedImage>(CreatePixelatedScreenshot);
        backgroundImage.Source = newShot.Bitmap;
        selectedAreaImage.Source = newShot.Bitmap;

        activeTool = EditorMemory.LastTool;
        currentColor = EditorMemory.LastColor;
        currentTextSize = EditorMemory.LastTextSize;
        shapesAreFilled = EditorMemory.ShapesAreFilled;

        toolbar.ShowTool(activeTool);
        toolbar.ShowColor(currentColor);
        toolbar.ShowFill(shapesAreFilled);
        toolbar.ShowTextSize(currentTextSize);
    }

    internal void CloseForGood()
    {
        isClosingForGood = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (!isClosingForGood && !isWarmUp && !App.IsExiting)
        {
            e.Cancel = true;     // e.g. Alt+F4: treat it like Esc instead of destroying a pooled window
            Dispatcher.BeginInvoke(new Action(session.Cancel));
        }
    }

    internal void ReleaseForPool()
    {
        ResetSelection();
        backgroundImage.Source = PlaceholderBitmap;
        selectedAreaImage.Source = PlaceholderBitmap;
        monitorShot = monitorShot with { Bitmap = PlaceholderBitmap };
        pixelatedScreenshot = new Lazy<PixelatedImage>(CreatePixelatedScreenshot);
        Hide();

        if (Content is Viewbox viewbox)
        {
            viewbox.Child = null;
            surface.ClearValue(WidthProperty);
            surface.ClearValue(HeightProperty);
            Content = surface;
        }
    }

    private static BitmapSource CreatePlaceholderBitmap()
    {
        var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
        bitmap.Freeze();
        return bitmap;
    }

    // Things CaptureSession needs
    public void BringToFront()
    {
        NativeMethods.ForceToForeground(windowHandle);
        Activate();
        Focus();
    }

    public void ResetSelection()
    {
        // Clear every drag state before letting go of the mouse, so OnLostMouseCapture finds nothing to finish.
        phase = Phase.WaitingForSelection;
        annotationBeingDragged = null;
        textBeingTyped = null;
        spaceHeld = false;
        draggedHandle = ResizeHandle.None;
        isMovingSelection = false;
        ReleaseMouseCapture();

        Cursor = Cursors.Cross;   // a resize/move/text cursor from last time must not stick around
        liveLayer.Show(Array.Empty<Annotation>());
        history.Clear();

        committedLayer.Clip = null;
        liveLayer.Clip = null;
        toolbar.Visibility = Visibility.Collapsed;

        selection = Rect.Empty;
        ShowSelection(null);
    }

    internal void RunWarmUpRenderPass()
    {
        UpdateLayout();   // make sure ActualWidth/ActualHeight are real numbers before we divide by them
        if (ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        _ = pixelatedScreenshot.Value;

        RenderContext context = BuildRenderContext();
        var sampleShape = new RectangleAnnotation(new Point(1, 1), Colors.Red, EditorDefaults.LineThickness, filled: false);
        sampleShape.UpdateDrag(new Point(4, 4));
        ScreenshotRenderer.Render(monitorShot.Bitmap, context.ScreenBounds, new Annotation[] { sampleShape }, context);
    }

    // Window setup
    private Grid BuildLayout()
    {
        toolbar.Visibility = Visibility.Collapsed;
        toolbarCanvas.Children.Add(toolbar);

        var layers = new Grid { ClipToBounds = true };
        layers.Children.Add(backgroundImage);
        layers.Children.Add(dimLayer);
        layers.Children.Add(selectedAreaImage);
        layers.Children.Add(selectionLayer);
        layers.Children.Add(committedLayer);
        layers.Children.Add(liveLayer);
        layers.Children.Add(toolbarCanvas);
        return layers;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        windowHandle = new WindowInteropHelper(this).Handle;
        RenderModes.ChooseForOverlay(this, MonitorBounds);
        PlaceOnMonitor();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(() => { if (IsVisible) PlaceOnMonitor(); }, DispatcherPriority.Send);
    }

    private void PlaceOnMonitor()
    {
        Int32Rect bounds = MonitorBounds;
        NativeMethods.SetWindowPos(
            windowHandle, NativeMethods.TopmostWindowGroup,
            bounds.X, bounds.Y, bounds.Width, bounds.Height, NativeMethods.DoNotActivate);
    }

    // Units

    private double PixelsPerUnitX => surface.ActualWidth > 0 ? monitorShot.Bitmap.PixelWidth / surface.ActualWidth : 1;

    private double PixelsPerUnitY => surface.ActualHeight > 0 ? monitorShot.Bitmap.PixelHeight / surface.ActualHeight : 1;

    private Point SnapToWholePixels(Point point) => new(
        Math.Round(point.X * PixelsPerUnitX) / PixelsPerUnitX,
        Math.Round(point.Y * PixelsPerUnitY) / PixelsPerUnitY);

    private Point KeepInsideWindow(Point point) => new(
        Math.Clamp(point.X, 0, surface.ActualWidth),
        Math.Clamp(point.Y, 0, surface.ActualHeight));

    private Point KeepInsideSelection(Point point) => new(
        Math.Clamp(point.X, selection.Left, selection.Right),
        Math.Clamp(point.Y, selection.Top, selection.Bottom));

    private RenderContext BuildRenderContext() => new(
        VisualTreeHelper.GetDpi(this).PixelsPerDip,
        new Rect(surface.RenderSize),
        pixelatedScreenshot,
        selection);

    private PixelatedImage CreatePixelatedScreenshot()
    {
        int blockSizeInPixels = (int)Math.Round(EditorDefaults.BlurBlockSize * PixelsPerUnitX);
        return ImagePixelator.Pixelate(monitorShot.Bitmap, blockSizeInPixels);
    }

    // Mouse
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point mousePosition = e.GetPosition(surface);
        lastRawMousePosition = mousePosition;

        switch (phase)
        {
            case Phase.WaitingForSelection:
                BeginSelection(mousePosition);
                break;

            case Phase.Annotating:
                HandleAnnotatingClick(mousePosition);
                break;
        }
    }

    private void HandleAnnotatingClick(Point mousePosition)
    {
        ResizeHandle handle = HitTestHandle(mousePosition);
        if (handle != ResizeHandle.None)
        {
            draggedHandle = handle;
            moveDragStartSelection = selection;
            resizeAnchor = handle switch
            {
                ResizeHandle.TopLeft => selection.BottomRight,
                ResizeHandle.TopRight => selection.BottomLeft,
                ResizeHandle.BottomLeft => selection.TopRight,
                _ => selection.TopLeft,
            };
            CaptureMouse();
            return;
        }

        if (!selection.Contains(mousePosition))
        {
            ResetSelection();
            BeginSelection(mousePosition);
            return;
        }

        if (activeTool is null)
        {
            isMovingSelection = true;
            moveDragStartMouse = mousePosition;
            moveDragStartSelection = selection;
            CaptureMouse();
            return;
        }

        BeginAnnotation(mousePosition);
    }

    private ResizeHandle HitTestHandle(Point point)
    {
        bool left = point.X <= selection.Left + selection.Width / 2;
        bool top = point.Y <= selection.Top + selection.Height / 2;
        Point nearest = new(left ? selection.Left : selection.Right, top ? selection.Top : selection.Bottom);
        if ((point - nearest).LengthSquared > HandleHitRadius * HandleHitRadius) return ResizeHandle.None;
        return CornerHandle(left, top);
    }

    private static ResizeHandle CornerHandle(bool left, bool top) =>
        top ? (left ? ResizeHandle.TopLeft : ResizeHandle.TopRight)
            : (left ? ResizeHandle.BottomLeft : ResizeHandle.BottomRight);

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        session.Cancel();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateDragAt(e.GetPosition(surface));
    }

    private void UpdateDragAt(Point mousePosition)
    {
        lastRawMousePosition = mousePosition;

        if (phase == Phase.DraggingSelection)
        {
            if (spaceHeld)
            {
                UpdateSpaceMove(mousePosition);
            }
            else
            {
                ResizeSelectionTo(mousePosition);
            }
        }
        else if (phase == Phase.Annotating)
        {
            if (draggedHandle != ResizeHandle.None)
            {
                UpdateResizingSelection(mousePosition);
            }
            else if (isMovingSelection)
            {
                UpdateMovingSelection(mousePosition);
            }
            else if (annotationBeingDragged is not null)
            {
                ApplyShiftConstraintToActiveShape();
                annotationBeingDragged.UpdateDrag(KeepInsideSelection(mousePosition));
                liveLayer.Refresh();
            }

            UpdateCursor(mousePosition);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (IsMouseCaptured)
        {
            UpdateDragAt(e.GetPosition(surface));
            ReleaseMouseCapture();   // OnLostMouseCapture finishes the drag
        }
        else
        {
            FinishAnyDrag();
        }
    }

    // Also runs when Windows takes the mouse away mid-drag (Alt+Tab, a popup...), so a drag can never get stuck.
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        FinishAnyDrag();
    }

    private void FinishAnyDrag()
    {
        if (phase == Phase.DraggingSelection)
        {
            FinishSelection();
        }
        else if (draggedHandle != ResizeHandle.None)
        {
            draggedHandle = ResizeHandle.None;
            if (SelectionIsTooSmall)
            {
                selection = moveDragStartSelection;
                ApplySelectionChange();
            }
            RememberSelectionIfEnabled();
        }
        else if (isMovingSelection)
        {
            isMovingSelection = false;
            RememberSelectionIfEnabled();
        }
        else if (annotationBeingDragged is not null)
        {
            FinishDraggedAnnotation();
        }
    }

    private void UpdateCursor(Point mousePosition)
    {
        ResizeHandle hoveredHandle = draggedHandle != ResizeHandle.None ? draggedHandle : HitTestHandle(mousePosition);
        if (hoveredHandle != ResizeHandle.None)
        {
            Cursor = hoveredHandle is ResizeHandle.TopLeft or ResizeHandle.BottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW;
            return;
        }

        if (isMovingSelection || (activeTool is null && selection.Contains(mousePosition)))
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        bool isTypingSpot = activeTool == EditorTool.Text && selection.Contains(mousePosition);
        Cursor = isTypingSpot ? Cursors.IBeam : Cursors.Cross;
    }

    // Selecting
    private void BeginSelection(Point mousePosition)
    {
        session.NotifySelectionStarting(this);

        if (!IsKeyboardFocusWithin)
        {
            BringToFront();   // multi-monitor: Ctrl+C / Esc must reach the overlay you're selecting on
        }

        selectionAnchor = SnapToWholePixels(KeepInsideWindow(mousePosition));
        selection = new Rect(selectionAnchor, selectionAnchor);
        phase = Phase.DraggingSelection;
        CaptureMouse();   // keep receiving moves even if the mouse leaves the window mid-drag

        ShowSelection(selection, BuildSizeLabel());
    }

    private void ResizeSelectionTo(Point mousePosition)
    {
        Point draggedCorner = SnapToWholePixels(KeepInsideWindow(mousePosition));
        selection = new Rect(selectionAnchor, draggedCorner);   // this Rect constructor sorts out which corner is which
        ShowSelection(selection, BuildSizeLabel());
    }

    private void BeginSpaceMove(Point rawMousePosition)
    {
        spaceHeld = true;
        spaceMoveStartMouse = rawMousePosition;
        spaceMoveStartSelection = selection;
    }

    private void UpdateSpaceMove(Point rawMousePosition)
    {
        Vector mouseDelta = rawMousePosition - spaceMoveStartMouse;
        Point movedTopLeft = SnapToWholePixels(ClampTopLeftInsideWindow(spaceMoveStartSelection.TopLeft + mouseDelta, spaceMoveStartSelection.Size));

        selection = new Rect(movedTopLeft, spaceMoveStartSelection.Size);
        ShowSelection(selection, BuildSizeLabel());
    }

    private void EndSpaceMove(Point rawMousePosition)
    {
        bool anchorWasOnLeftEdge = Math.Abs(selectionAnchor.X - spaceMoveStartSelection.Left) <= Math.Abs(selectionAnchor.X - spaceMoveStartSelection.Right);
        bool anchorWasOnTopEdge = Math.Abs(selectionAnchor.Y - spaceMoveStartSelection.Top) <= Math.Abs(selectionAnchor.Y - spaceMoveStartSelection.Bottom);

        selectionAnchor = new Point(
            anchorWasOnLeftEdge ? selection.Left : selection.Right,
            anchorWasOnTopEdge ? selection.Top : selection.Bottom);

        spaceHeld = false;
        ResizeSelectionTo(rawMousePosition);
    }

    private void UpdateResizingSelection(Point mousePosition)
    {
        Point point = SnapToWholePixels(KeepInsideWindow(mousePosition));
        selection = new Rect(resizeAnchor, point);
        draggedHandle = CornerHandle(point.X < resizeAnchor.X, point.Y < resizeAnchor.Y);
        ApplySelectionChange();
    }

    private void UpdateMovingSelection(Point mousePosition)
    {
        Vector delta = mousePosition - moveDragStartMouse;
        Point topLeft = SnapToWholePixels(ClampTopLeftInsideWindow(moveDragStartSelection.TopLeft + delta, moveDragStartSelection.Size));

        selection = new Rect(topLeft, moveDragStartSelection.Size);
        ApplySelectionChange();
    }

    private void ApplySelectionChange()
    {
        ShowSelection(selection);

        var clipToSelection = new RectangleGeometry(selection);
        clipToSelection.Freeze();
        committedLayer.Clip = clipToSelection;
        liveLayer.Clip = clipToSelection;

        PositionToolbar();
    }

    private Point ClampTopLeftInsideWindow(Point topLeft, Size size) => new(
        Math.Clamp(topLeft.X, 0, Math.Max(0, surface.ActualWidth - size.Width)),
        Math.Clamp(topLeft.Y, 0, Math.Max(0, surface.ActualHeight - size.Height)));

    private void ApplyShiftConstraintToActiveShape()
    {
        if (annotationBeingDragged is TwoPointAnnotation twoPointAnnotation)
        {
            twoPointAnnotation.IsConstrained = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        }
    }

    private bool SelectionIsTooSmall => selection.Width < EditorDefaults.MinimumSelectionSize
                                        || selection.Height < EditorDefaults.MinimumSelectionSize;

    private void FinishSelection()
    {
        spaceHeld = false;   // dragging is over; a stale Space state must never leak into the Annotating phase

        if (SelectionIsTooSmall)
        {
            ResetSelection();
            return;
        }

        phase = Phase.Annotating;
        RememberSelectionIfEnabled();
        ShowSelection(selection);   // no label now, the toolbar takes that spot

        var clipToSelection = new RectangleGeometry(selection);
        clipToSelection.Freeze();
        committedLayer.Clip = clipToSelection;
        liveLayer.Clip = clipToSelection;

        toolbar.Visibility = Visibility.Visible;
        PositionToolbar();
    }

    // "Keep the selected area position"
    private void RememberSelectionIfEnabled()
    {
        if (isWarmUp || !session.Settings.KeepSelectedAreaPosition)
        {
            return;
        }

        var pixelArea = new Int32Rect(
            (int)Math.Round(selection.X * PixelsPerUnitX),
            (int)Math.Round(selection.Y * PixelsPerUnitY),
            (int)Math.Round(selection.Width * PixelsPerUnitX),
            (int)Math.Round(selection.Height * PixelsPerUnitY));

        EditorMemory.LastSelection = new RememberedSelection(monitorShot.Bounds, pixelArea);
    }

    internal bool RestoreRememberedSelection()
    {
        if (isWarmUp || phase != Phase.WaitingForSelection
            || !session.Settings.KeepSelectedAreaPosition)
        {
            return false;
        }

        if (EditorMemory.LastSelection is not RememberedSelection remembered
            || !remembered.MonitorBounds.Equals(monitorShot.Bounds)
            || surface.ActualWidth < 1 || surface.ActualHeight < 1)
        {
            return false;
        }

        Int32Rect area = remembered.PixelArea;
        bool stillFitsOnThisMonitor = area.X >= 0 && area.Y >= 0
                                      && area.X + area.Width <= monitorShot.Bitmap.PixelWidth
                                      && area.Y + area.Height <= monitorShot.Bitmap.PixelHeight;
        if (!stillFitsOnThisMonitor)
        {
            return false;
        }

        session.NotifySelectionStarting(this);
        selection = new Rect(
            area.X / PixelsPerUnitX, area.Y / PixelsPerUnitY,
            area.Width / PixelsPerUnitX, area.Height / PixelsPerUnitY);

        FinishSelection();
        return phase == Phase.Annotating;
    }

    private void SelectWholeMonitor()
    {
        CommitActiveDrag();
        session.NotifySelectionStarting(this);
        selection = new Rect(surface.RenderSize);
        FinishSelection();
    }

    // Moving the selection only changes the clip of the undimmed image and the small outline layer,
    // so each frame repaints just the area around the selection instead of the whole dimmed screen.
    private void ShowSelection(Rect? area, string? sizeLabel = null)
    {
        selectionLayer.Update(area, sizeLabel);

        if (area is Rect visibleArea && !visibleArea.IsEmpty)
        {
            var clip = new RectangleGeometry(visibleArea, EditorDefaults.SelectionCornerRadius, EditorDefaults.SelectionCornerRadius);
            clip.Freeze();
            selectedAreaImage.Clip = clip;
        }
        else
        {
            selectedAreaImage.Clip = Geometry.Empty;
        }
    }

    private string BuildSizeLabel() =>
        $"{Math.Round(selection.Width * PixelsPerUnitX)} \u00D7 {Math.Round(selection.Height * PixelsPerUnitY)}";

    private void PositionToolbar()
    {
        toolbar.MaxWidth = Math.Max(1, surface.ActualWidth - 2 * ToolbarScreenMargin);
        toolbar.Measure(new Size(toolbar.MaxWidth, double.PositiveInfinity));
        Size toolbarSize = toolbar.DesiredSize;

        double left = selection.Left + (selection.Width - toolbarSize.Width) / 2;
        double rightmostLeft = Math.Max(ToolbarScreenMargin, surface.ActualWidth - toolbarSize.Width - ToolbarScreenMargin);
        left = Math.Clamp(left, ToolbarScreenMargin, rightmostLeft);

        double top = selection.Top - toolbarSize.Height - ToolbarGap;
        if (top < ToolbarScreenMargin)
        {
            top = selection.Bottom + ToolbarGap;

            bool fitsBelow = top + toolbarSize.Height <= surface.ActualHeight - ToolbarScreenMargin;
            if (!fitsBelow)
            {
                top = selection.Bottom - toolbarSize.Height - ToolbarGap;
            }
        }

        Canvas.SetLeft(toolbar, Math.Round(left));
        double lowestTop = Math.Max(0, surface.ActualHeight - toolbarSize.Height);
        Canvas.SetTop(toolbar, Math.Round(Math.Clamp(top, 0, lowestTop)));
    }

    // Annotating
    private void BeginAnnotation(Point mousePosition)
    {
        if (activeTool is not EditorTool tool)
        {
            return;
        }

        FinishTypedText();   // clicking somewhere new locks in whatever text was being typed

        Point startPoint = KeepInsideSelection(mousePosition);

        if (tool == EditorTool.Text)
        {
            StartTypingText(startPoint, currentColor);
            return;
        }

        annotationBeingDragged = CreateDragAnnotation(tool, startPoint, currentColor);
        liveLayer.Show(new Annotation[] { annotationBeingDragged });
        CaptureMouse();
    }

    private DragAnnotation CreateDragAnnotation(EditorTool tool, Point startPoint, Color color) => tool switch
    {
        EditorTool.Draw => new StrokeAnnotation(
            startPoint, DrawingTools.CreatePen(color, EditorDefaults.LineThickness)),

        EditorTool.Highlight => new StrokeAnnotation(
            startPoint,
            DrawingTools.CreatePen(
                Color.FromArgb(EditorDefaults.HighlightOpacity, color.R, color.G, color.B),
                EditorDefaults.HighlightThickness,
                PenLineCap.Flat)),

        EditorTool.Arrow => new ArrowAnnotation(startPoint, color, EditorDefaults.LineThickness),
        EditorTool.Rectangle => new RectangleAnnotation(startPoint, color, EditorDefaults.LineThickness, shapesAreFilled),
        EditorTool.Ellipse => new EllipseAnnotation(startPoint, color, EditorDefaults.LineThickness, shapesAreFilled),
        EditorTool.Blur => new BlurAnnotation(startPoint),

        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "This tool doesn't draw by dragging."),
    };

    private void FinishDraggedAnnotation()
    {
        DragAnnotation finishedAnnotation = annotationBeingDragged!;
        annotationBeingDragged = null;

        liveLayer.Show(Array.Empty<Annotation>());

        if (finishedAnnotation is TwoPointAnnotation shape) shape.CommitConstraint(selection);

        if (finishedAnnotation.IsWorthKeeping)
        {
            history.Add(finishedAnnotation);
        }
    }

    // Text typing
    private void StartTypingText(Point position, Color color)
    {
        textBeingTyped = new TextAnnotation(position, color, currentTextSize) { IsEditing = true };
        liveLayer.Show(new Annotation[] { textBeingTyped });
    }

    private void FinishTypedText()
    {
        if (textBeingTyped is null) return;

        TextAnnotation finishedText = textBeingTyped;
        textBeingTyped = null;
        finishedText.IsEditing = false;

        liveLayer.Show(Array.Empty<Annotation>());

        if (finishedText.HasText)
        {
            history.Add(finishedText);
        }
    }

    private void DiscardTypedText()
    {
        textBeingTyped = null;
        liveLayer.Show(Array.Empty<Annotation>());
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (textBeingTyped is null) return;

        foreach (char character in e.Text)
        {
            if (!char.IsControl(character))
            {
                textBeingTyped.Append(character.ToString());
            }
        }

        liveLayer.Refresh();
        e.Handled = true;
    }

    // Keyboard
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (IsShiftKey(e.Key))
        {
            ApplyShiftConstraintToActiveShape();
            liveLayer.Refresh();
            return;
        }

        if (e.Key == Key.System && e.SystemKey == Key.F4)
        {
            session.Cancel();     // Alt+F4 = Esc
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && phase == Phase.DraggingSelection)
        {
            if (!spaceHeld && !e.IsRepeat)
            {
                BeginSpaceMove(lastRawMousePosition);
            }

            e.Handled = true;
            return;
        }

        bool ctrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (textBeingTyped is not null && HandleKeyWhileTyping(e.Key, ctrlHeld, shiftHeld))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                session.Cancel();
                e.Handled = true;
                break;

            case Key.C when ctrlHeld:
                FinishWith(FinishAction.CopyToClipboard);
                e.Handled = true;
                break;

            case Key.S when ctrlHeld:
                FinishWith(FinishAction.SaveToFile);
                e.Handled = true;
                break;

            case Key.A when ctrlHeld && textBeingTyped is null && phase != Phase.DraggingSelection:
                SelectWholeMonitor();
                e.Handled = true;
                break;

            case Key.Z when ctrlHeld && shiftHeld:
                Redo();
                e.Handled = true;
                break;

            case Key.Z when ctrlHeld:
                Undo();
                e.Handled = true;
                break;

            case Key.Y when ctrlHeld:
                Redo();
                e.Handled = true;
                break;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);

        if (IsShiftKey(e.Key))
        {
            ApplyShiftConstraintToActiveShape();
            liveLayer.Refresh();
            return;
        }

        if (e.Key == Key.Space && spaceHeld && phase == Phase.DraggingSelection)
        {
            EndSpaceMove(lastRawMousePosition);
            e.Handled = true;
        }
    }

    private static bool IsShiftKey(Key key) => key is Key.LeftShift or Key.RightShift;

    private bool HandleKeyWhileTyping(Key key, bool ctrlHeld, bool shiftHeld)
    {
        switch (key)
        {
            case Key.Escape:                    // Esc while typing only cancels the text. Esc again closes the screenshot.
                DiscardTypedText();
                return true;

            case Key.Enter when shiftHeld:      // Shift+Enter = new line
                textBeingTyped!.AppendNewLine();
                liveLayer.Refresh();
                return true;

            case Key.Enter:                     // Enter = done typing
                FinishTypedText();
                return true;

            case Key.Back:
                textBeingTyped!.RemoveLastCharacter();
                liveLayer.Refresh();
                return true;

            case Key.Z when ctrlHeld:
                DiscardTypedText();
                return true;

            default:
                return false;
        }
    }

    // Actions
    private void CommitActiveDrag()
    {
        FinishAnyDrag();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Undo()
    {
        if (textBeingTyped is not null)
        {
            DiscardTypedText();
            return;
        }

        history.Undo();
    }

    private void Redo()
    {
        FinishTypedText();
        history.Redo();
    }

    private void FinishWith(FinishAction action)
    {
        if (phase != Phase.Annotating) return;

        CommitActiveDrag();
        FinishTypedText();

        BitmapSource finalImage = ScreenshotRenderer.Render(
            monitorShot.Bitmap, selection, history.Applied, BuildRenderContext());

        LastCaptureMemory.Value = new LastCapture(monitorShot, surface.RenderSize, selection, new List<Annotation>(history.Applied));

        session.Finish(finalImage, action);
    }

    internal void RestoreAnnotatedSelection(Rect restoredSelection, IReadOnlyList<Annotation> annotations)
    {
        selection = restoredSelection;
        history.Restore(annotations);
        phase = Phase.Annotating;

        committedLayer.Show(history.Applied);
        toolbar.Visibility = Visibility.Visible;   // must be visible before PositionToolbar measures it
        ApplySelectionChange();

        toolbar.ShowTool(activeTool);
        toolbar.ShowColor(currentColor);
        toolbar.ShowFill(shapesAreFilled);
        toolbar.ShowTextSize(currentTextSize);
        toolbar.SetUndoRedoAvailability(history.CanUndo, history.CanRedo);
    }

    // Toolbar events
    private void OnHistoryChanged()
    {
        committedLayer.Refresh();
        toolbar.SetUndoRedoAvailability(history.CanUndo, history.CanRedo);
    }

    private void OnToolChanged(EditorTool? newTool)
    {
        FinishTypedText();
        activeTool = newTool;
        EditorMemory.LastTool = newTool;
    }

    private void OnColorPicked(Color color)
    {
        currentColor = color;
        EditorMemory.LastColor = color;

        if (textBeingTyped is not null)
        {
            textBeingTyped.ChangeColor(color);
            liveLayer.Refresh();
        }
    }

    private void OnFillToggled(bool isFilled)
    {
        shapesAreFilled = isFilled;
        EditorMemory.ShapesAreFilled = isFilled;
    }

    private void OnTextSizeChanged(double size)
    {
        currentTextSize = size;
        EditorMemory.LastTextSize = size;
        if (textBeingTyped is not null)
        {
            textBeingTyped.FontSize = size;
            liveLayer.Refresh();
        }
    }
}
