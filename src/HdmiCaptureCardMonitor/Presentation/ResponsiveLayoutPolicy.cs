namespace HdmiCaptureCardMonitor.Presentation;

internal static class ResponsiveLayoutPolicy
{
    internal static bool UsesStackedSelectors(double availableWidth) =>
        double.IsFinite(availableWidth) && availableWidth < LayoutMetrics.NarrowWindowThreshold;
}
