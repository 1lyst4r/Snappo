using System.Collections.Generic;
using System.Windows;
using Snappo.Capture;
using Snappo.Editor.Annotations;

namespace Snappo.Editor;

internal sealed record LastCapture(MonitorShot MonitorShot, Size EditorSize, Rect Selection, IReadOnlyList<Annotation> Annotations);

internal static class LastCaptureMemory
{
    public static LastCapture? Value { get; set; }
}
