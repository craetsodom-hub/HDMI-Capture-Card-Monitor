using System.Windows;

namespace HdmiCaptureCardMonitor.Presentation;

internal static class LayoutMetrics
{
    internal const double MinimumWindowWidth = 720;
    internal const double NarrowWindowThreshold = 900;
    internal const double DefaultWindowWidth = 1180;
    internal const double PagePadding = 24;
    internal const double CardPadding = 16;
    internal const double ControlGap = 12;
    internal const double StackedSectionGap = 16;
    internal const double StandardButtonMinimumWidth = 88;
    internal const double PrimaryButtonMinimumWidth = 112;

    internal static GridLength StarColumn => new(1, GridUnitType.Star);
    internal static GridLength WideControlGapColumn => new(ControlGap);
    internal static GridLength CollapsedColumn => new(0);
    internal static Thickness StackedSectionMargin => new(0, StackedSectionGap, 0, 0);
    internal static Thickness NoMargin => new(0);
}
