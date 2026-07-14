using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace HdmiCaptureCardMonitor.Capture.Interop;

internal sealed class MediaFoundationDeviceDiscoveryService : ICaptureDeviceDiscoveryService
{
    private readonly IApplicationLogger logger;
    private readonly ActiveOperationRegistry operations;

    public MediaFoundationDeviceDiscoveryService(IApplicationLogger logger)
        : this(logger, new ActiveOperationRegistry())
    {
    }

    internal MediaFoundationDeviceDiscoveryService(IApplicationLogger logger, ActiveOperationRegistry operations)
    {
        this.logger = logger;
        this.operations = operations;
    }

    public bool WorkersSettled => operations.WorkersSettled;

    public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken) =>
        RunAsync(EnumerateVideoDevices, cancellationToken);

    public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken) =>
        RunAsync(token => GetNativeVideoCapabilities(device, token), cancellationToken);

    public void Dispose()
    {
        operations.Dispose();
        if (!operations.WorkersSettled)
        {
            logger.Warning("Media Foundation discovery workers did not finish within the three-second shutdown bound; Media Foundation shutdown must be skipped.");
        }
    }

    private Task<DiscoveryResult<T>> RunAsync<T>(Func<CancellationToken, DiscoveryResult<T>> operation, CancellationToken cancellationToken)
    {
        var task = operations.TryStart(operation, cancellationToken);
        return task is null ? Task.FromResult(DiscoveryResults.Cancelled<T>()) : AwaitOperationAsync(task);
    }

    private static async Task<DiscoveryResult<T>> AwaitOperationAsync<T>(Task<DiscoveryResult<T>> operation)
    {
        try { return await operation.ConfigureAwait(false); }
        catch (OperationCanceledException) { return DiscoveryResults.Cancelled<T>(); }
    }

    private unsafe DiscoveryResult<IReadOnlyList<CaptureDevice>> EnumerateVideoDevices(CancellationToken cancellationToken)
    {
        var apartmentResult = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (apartmentResult.Failed)
        {
            return FailedDevices(DiscoveryFailureCategory.ComApartmentFailure, apartmentResult.Value, "The discovery worker could not initialize COM.");
        }

        try
        {
            var createResult = PInvoke.MFCreateAttributes(out IMFAttributes? attributes, 1);
            if (createResult.Failed || attributes is null)
            {
                return FailedDevices(DiscoveryFailureCategory.Unknown, createResult.Value, "Could not create Media Foundation device attributes.");
            }

            try
            {
                try { attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID); }
                catch (COMException exception) { return FailedDevices(MapFailure(exception.HResult), exception.HResult, "Could not configure video-device enumeration attributes.", exception); }

                var enumerateResult = PInvoke.MFEnumDeviceSources(attributes, out IMFActivate_unmanaged** activates, out uint count);
                if (enumerateResult.Failed)
                {
                    return FailedDevices(MapFailure(enumerateResult.Value), enumerateResult.Value, "Windows could not enumerate video capture devices.");
                }

                try
                {
                    var devices = new List<CaptureDevice>();
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var activate = activates[index];
                        if (activate is null) continue;

                        var linkResult = ReadRequiredString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                        if (!linkResult.IsSuccess)
                        {
                            logger.Warning("A video-device activation was ignored because its required identifier was unavailable.");
                            continue;
                        }

                        var friendlyName = ReadOptionalString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                        var hardwareSource = ReadOptionalUInt32(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_HW_SOURCE);
                        devices.Add(new CaptureDevice(
                            linkResult.Value!,
                            string.IsNullOrWhiteSpace(friendlyName) ? "Unnamed video device" : friendlyName,
                            string.Empty,
                            hardwareSource is null ? null : hardwareSource != 0,
                            CaptureBackend.MediaFoundation));
                    }

                    return DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>(CaptureDeviceNormalizer.Normalize(devices));
                }
                finally
                {
                    if (activates is not null)
                    {
                        for (var index = 0; index < count; index++)
                        {
                            if (activates[index] is not null) activates[index]->Release();
                        }
                        PInvoke.CoTaskMemFree(activates);
                    }
                }
            }
            finally { ReleaseComObject(attributes); }
        }
        finally { PInvoke.CoUninitialize(); }
    }

    private unsafe DiscoveryResult<IReadOnlyList<NativeVideoCapability>> GetNativeVideoCapabilities(CaptureDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var apartmentResult = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (apartmentResult.Failed)
        {
            return FailedCapabilities(DiscoveryFailureCategory.ComApartmentFailure, apartmentResult.Value, "The discovery worker could not initialize COM.");
        }

        try
        {
            var createAttributesResult = PInvoke.MFCreateAttributes(out IMFAttributes? attributes, 1);
            if (createAttributesResult.Failed || attributes is null)
            {
                return FailedCapabilities(DiscoveryFailureCategory.Unknown, createAttributesResult.Value, "Could not create device activation attributes.");
            }

            IMFMediaSource? mediaSource = null;
            IMFSourceReader? sourceReader = null;
            try
            {
                try
                {
                    attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
                    attributes.SetString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, device.Id);
                }
                catch (COMException exception)
                {
                    return FailedCapabilities(MapFailure(exception.HResult), exception.HResult, "Could not configure selected-device activation attributes.", exception);
                }

                var createSourceResult = PInvoke.MFCreateDeviceSource(attributes, out mediaSource);
                if (createSourceResult.Failed || mediaSource is null)
                {
                    return FailedCapabilities(MapFailure(createSourceResult.Value), createSourceResult.Value, "The selected video device could not be opened.");
                }

                var createReaderResult = PInvoke.MFCreateSourceReaderFromMediaSource(mediaSource, null, out sourceReader);
                if (createReaderResult.Failed || sourceReader is null)
                {
                    return FailedCapabilities(MapFailure(createReaderResult.Value), createReaderResult.Value, "Could not inspect the selected device formats.");
                }

                var capabilities = new List<NativeVideoCapability>();
                for (var mediaTypeIndex = 0; ; mediaTypeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IMFMediaType? mediaType = null;
                    try
                    {
                        try
                        {
                            sourceReader.GetNativeMediaType(
                                unchecked((uint)(int)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                                (uint)mediaTypeIndex,
                                out mediaType);
                        }
                        catch (COMException exception) when (exception.HResult == MediaFoundationHResults.NoMoreTypes)
                        {
                            break;
                        }
                        catch (COMException exception)
                        {
                            return FailedCapabilities(MapFailure(exception.HResult), exception.HResult, "Native media-type enumeration failed.", exception);
                        }

                        if (mediaType is null)
                        {
                            return FailedCapabilities(DiscoveryFailureCategory.InvalidNativeData, null, "Native media-type enumeration returned no media type.");
                        }

                        if (TryCreateCapability(mediaType, mediaTypeIndex, out var capability)) capabilities.Add(capability!);
                        else logger.Warning($"Native video media type {mediaTypeIndex} was ignored because mandatory metadata was invalid.");
                    }
                    finally { ReleaseComObject(mediaType); }
                }

                var normalized = NativeVideoCapabilityFormatter.SortAndDeduplicate(capabilities);
                return normalized.Count == 0
                    ? FailedCapabilities(DiscoveryFailureCategory.NoUsableFormats, null, "The device exposed no usable native video capabilities.")
                    : DiscoveryResults.Success(normalized);
            }
            finally
            {
                ReleaseComObject(sourceReader);
                ShutdownAndReleaseMediaSource(mediaSource);
                ReleaseComObject(attributes);
            }
        }
        finally { PInvoke.CoUninitialize(); }
    }

    private static bool TryCreateCapability(IMFMediaType mediaType, int mediaTypeIndex, out NativeVideoCapability? capability)
    {
        capability = null;
        try
        {
            mediaType.GetGUID(PInvoke.MF_MT_MAJOR_TYPE, out var majorType);
            if (majorType != PInvoke.MFMediaType_Video) return false;
            mediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
            mediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var frameSize);
            mediaType.GetUINT64(PInvoke.MF_MT_FRAME_RATE, out var frameRate);

            var width = (uint)(frameSize >> 32);
            var height = (uint)frameSize;
            var numerator = (uint)(frameRate >> 32);
            var denominator = (uint)frameRate;
            if (subtype == Guid.Empty || width == 0 || height == 0 || denominator == 0) return false;

            var interlace = ReadInterlaceMode(mediaType);
            var (aspectNumerator, aspectDenominator) = ReadPixelAspectRatio(mediaType);
            var subtypeLabel = GetSubtypeLabel(subtype);
            capability = new NativeVideoCapability(
                0,
                mediaTypeIndex,
                width,
                height,
                numerator,
                denominator,
                NativeVideoCapabilityFormatter.CalculateFrameRate(numerator, denominator),
                subtype,
                subtypeLabel,
                interlace,
                aspectNumerator,
                aspectDenominator,
                NativeVideoCapabilityFormatter.CreateDisplayLabel(width, height, numerator, denominator, subtypeLabel, interlace));
            return true;
        }
        catch (COMException) { return false; }
    }

    internal static VideoInterlaceMode MapInterlaceMode(uint value) => value switch
    {
        2 => VideoInterlaceMode.Progressive,
        3 or 4 or 5 or 6 => VideoInterlaceMode.Interlaced,
        7 => VideoInterlaceMode.Mixed,
        _ => VideoInterlaceMode.Unknown
    };

    private static VideoInterlaceMode ReadInterlaceMode(IMFMediaType mediaType)
    {
        try { mediaType.GetUINT32(PInvoke.MF_MT_INTERLACE_MODE, out var value); return MapInterlaceMode(value); }
        catch (COMException) { return VideoInterlaceMode.Unknown; }
    }

    private static (uint Numerator, uint Denominator) ReadPixelAspectRatio(IMFMediaType mediaType)
    {
        try
        {
            mediaType.GetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, out var value);
            return ((uint)(value >> 32), (uint)value);
        }
        catch (COMException) { return (0, 0); }
    }

    private unsafe static DiscoveryResult<string> ReadRequiredString(IMFActivate_unmanaged* activate, Guid key)
    {
        uint length;
        try { activate->GetStringLength(key, out length); }
        catch (COMException exception) { return DiscoveryResults.Failed<string>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, MapFailure(exception.HResult), exception.HResult, "A required device attribute length was unavailable.", exception)); }
        if (length == 0) return DiscoveryResults.Failed<string>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, DiscoveryFailureCategory.InvalidNativeData, null, "A required device attribute was empty."));

        var buffer = new char[length + 1];
        var actualLength = 0u;
        fixed (char* pointer = buffer)
        {
            try { activate->GetString(&key, pointer, length + 1, &actualLength); }
            catch (COMException exception) { return DiscoveryResults.Failed<string>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, MapFailure(exception.HResult), exception.HResult, "A required device attribute was unavailable.", exception)); }
        }

        var value = new string(buffer, 0, checked((int)Math.Min(actualLength, length)));
        return string.IsNullOrWhiteSpace(value)
            ? DiscoveryResults.Failed<string>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, DiscoveryFailureCategory.InvalidNativeData, null, "A required device attribute was empty."))
            : DiscoveryResults.Success(value);
    }

    private unsafe static string? ReadOptionalString(IMFActivate_unmanaged* activate, Guid key)
    {
        var result = ReadRequiredString(activate, key);
        return result.IsSuccess ? result.Value : null;
    }

    private unsafe static uint? ReadOptionalUInt32(IMFActivate_unmanaged* activate, Guid key)
    {
        try { activate->GetUINT32(key, out var value); return value; }
        catch (COMException) { return null; }
    }

    private void ShutdownAndReleaseMediaSource(IMFMediaSource? mediaSource)
    {
        if (mediaSource is null) return;
        try { mediaSource.Shutdown(); }
        catch (COMException exception) when (IsAlreadyShutdownResult(exception.HResult)) { }
        catch (COMException exception) { logger.Warning($"Media source shutdown reported 0x{exception.HResult:X8}; continuing deterministic native cleanup."); }
        finally { ReleaseComObject(mediaSource); }
    }

    internal static bool IsAlreadyShutdownResult(int hresult) => hresult == MediaFoundationHResults.Shutdown;

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static string GetSubtypeLabel(Guid subtype)
    {
        if (subtype == PInvoke.MFVideoFormat_MJPG) return "MJPEG";
        if (subtype == PInvoke.MFVideoFormat_YUY2) return "YUY2";
        if (subtype == PInvoke.MFVideoFormat_NV12) return "NV12";
        if (subtype == PInvoke.MFVideoFormat_RGB32) return "RGB32";
        if (subtype == PInvoke.MFVideoFormat_RGB24) return "RGB24";
        if (subtype == PInvoke.MFVideoFormat_H264) return "H.264";
        return $"Other · {subtype:N}"[..16];
    }

    private static DiscoveryFailureCategory MapFailure(int hresult) => hresult == MediaFoundationHResults.EAccessDenied
        ? DiscoveryFailureCategory.AccessDenied
        : DiscoveryFailureCategory.Unknown;

    private static DiscoveryResult<IReadOnlyList<CaptureDevice>> FailedDevices(DiscoveryFailureCategory category, int? hresult, string message, Exception? exception = null) =>
        DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, category, hresult, message, exception));

    private static DiscoveryResult<IReadOnlyList<NativeVideoCapability>> FailedCapabilities(DiscoveryFailureCategory category, int? hresult, string message, Exception? exception = null) =>
        DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, category, hresult, message, exception));
}
