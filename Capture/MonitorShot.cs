using System.Windows;
using System.Windows.Media.Imaging;

namespace Snappo.Capture;

internal sealed record MonitorShot(Int32Rect Bounds, BitmapSource Bitmap, string SourceApp = "Desktop");
