namespace HdmiCaptureCardMonitor.Models;

/// <summary>Immutable native video media type exposed by a selected capture device.</summary>
public sealed record NativeVideoCapability(
    int StreamIndex,
    int NativeMediaTypeIndex,
    uint Width,
    uint Height,
    uint FrameRateNumerator,
    uint FrameRateDenominator,
    double ExactFrameRate,
    Guid MediaSubtype,
    string SubtypeLabel,
    VideoInterlaceMode InterlaceMode,
    uint PixelAspectRatioNumerator,
    uint PixelAspectRatioDenominator,
    string DisplayLabel);
