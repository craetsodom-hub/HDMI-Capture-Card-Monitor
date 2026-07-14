namespace HdmiCaptureCardMonitor.Presentation;

internal static class ResponsiveLayoutPolicy
{
    internal const double NarrowWindowThreshold = 900;

    internal static bool UsesStackedSelectors(double availableWidth) =>
        double.IsFinite(availableWidth) && availableWidth < NarrowWindowThreshold;
}
