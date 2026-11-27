using System;
using System.Windows;

namespace Snappo.Editor;

internal sealed record RenderContext(double PixelsPerDip, Rect ScreenBounds, Lazy<PixelatedImage> PixelatedScreenshot, Rect SelectionBounds);
