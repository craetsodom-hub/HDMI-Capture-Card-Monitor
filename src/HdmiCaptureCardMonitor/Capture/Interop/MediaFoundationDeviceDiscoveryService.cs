using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Devices;
using HdmiCaptureCardMonitor.Models;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace HdmiCaptureCardMonitor.Capture.Interop;

internal sealed unsafe class MediaFoundationDeviceDiscoveryService : ICaptureDeviceDiscoveryService
{
    private readonly MediaFoundationRuntime runtime;

    public MediaFoundationDeviceDiscoveryService(MediaFoundationRuntime runtime)
    {
        this.runtime = runtime;
    }

    public Task<DiscoveryResult<IReadOnlyList<CaptureDevice>>> EnumerateVideoDevicesAsync(CancellationToken cancellationToken) =>
        Task.Run(() => EnumerateVideoDevices(cancellationToken), cancellationToken);

    public Task<DiscoveryResult<IReadOnlyList<NativeVideoCapability>>> GetNativeVideoCapabilitiesAsync(CaptureDevice device, CancellationToken cancellationToken) =>
        Task.Run(() => GetNativeVideoCapabilities(device, cancellationToken), cancellationToken);

    public void Dispose()
    {
    }

    private DiscoveryResult<IReadOnlyList<CaptureDevice>> EnumerateVideoDevices(CancellationToken cancellationToken)
    {
        var runtimeResult = runtime.EnsureStarted();
        if (!runtimeResult.IsSuccess)
        {
            return DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(runtimeResult.Failure!);
        }

        var apartmentResult = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (apartmentResult.Failed)
        {
            return DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, apartmentResult.Value, "The discovery worker could not initialize COM."));
        }

        try
        {
            IMFAttributes? attributes;
            var createResult = PInvoke.MFCreateAttributes(out attributes, 1);
            if (createResult.Failed || attributes is null)
            {
                return DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, createResult.Value, "Could not create Media Foundation device attributes."));
            }

            try
            {
                attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

                IMFActivate_unmanaged** activates;
                uint count;
                var enumerateResult = PInvoke.MFEnumDeviceSources(attributes, out activates, out count);
                if (enumerateResult.Failed)
                {
                    return DiscoveryResults.Failed<IReadOnlyList<CaptureDevice>>(new DiscoveryFailure(DiscoveryOperation.DeviceEnumeration, enumerateResult.Value, "Windows could not enumerate video capture devices."));
                }

                var devices = new List<CaptureDevice>();
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var activate = activates[index];
                        uint friendlyNameLength;
                        activate->GetStringLength(PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, out friendlyNameLength);
                        var friendlyNameBuffer = new char[friendlyNameLength + 1];
                        uint linkLength;
                        activate->GetStringLength(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, out linkLength);
                        var linkBuffer = new char[linkLength + 1];
                        var friendlyNameKey = PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME;
                        var symbolicLinkKey = PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK;
                        uint actualFriendlyNameLength;
                        uint actualLinkLength;
                        fixed (char* friendlyNamePointer = friendlyNameBuffer)
                        fixed (char* linkPointer = linkBuffer)
                        {
                            activate->GetString(&friendlyNameKey, friendlyNamePointer, friendlyNameLength + 1, &actualFriendlyNameLength);
                            activate->GetString(&symbolicLinkKey, linkPointer, linkLength + 1, &actualLinkLength);
                        }

                        devices.Add(new CaptureDevice(new string(linkBuffer, 0, (int)actualLinkLength), new string(friendlyNameBuffer, 0, (int)actualFriendlyNameLength), string.Empty, null, CaptureBackend.MediaFoundation));
                    }
                }
                finally
                {
                    for (var index = 0; index < count; index++)
                    {
                        activates[index]->Release();
                    }

                    PInvoke.CoTaskMemFree(activates);
                }

                return DiscoveryResults.Success<IReadOnlyList<CaptureDevice>>(Capture.Devices.CaptureDeviceNormalizer.Normalize(devices));
            }
            finally
            {
                if (Marshal.IsComObject(attributes))
                {
                    Marshal.ReleaseComObject(attributes);
                }
            }
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    private DiscoveryResult<IReadOnlyList<NativeVideoCapability>> GetNativeVideoCapabilities(CaptureDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var runtimeResult = runtime.EnsureStarted();
        if (!runtimeResult.IsSuccess)
        {
            return DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(runtimeResult.Failure!);
        }

        var apartmentResult = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (apartmentResult.Failed)
        {
            return DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, apartmentResult.Value, "The discovery worker could not initialize COM."));
        }

        try
        {
            IMFAttributes? attributes;
            var createAttributesResult = PInvoke.MFCreateAttributes(out attributes, 1);
            if (createAttributesResult.Failed || attributes is null)
            {
                return DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(new DiscoveryFailure(DiscoveryOperation.SelectedDeviceActivation, createAttributesResult.Value, "Could not create device activation attributes."));
            }

            IMFMediaSource? mediaSource = null;
            IMFSourceReader? sourceReader = null;
            try
            {
                attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
                attributes.SetString(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, device.Id);
                var createSourceResult = PInvoke.MFCreateDeviceSource(attributes, out mediaSource);
                if (createSourceResult.Failed || mediaSource is null)
                {
                    return DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(new DiscoveryFailure(DiscoveryOperation.SelectedDeviceActivation, createSourceResult.Value, "The selected video device could not be opened."));
                }

                var createReaderResult = PInvoke.MFCreateSourceReaderFromMediaSource(mediaSource, null, out sourceReader);
                if (createReaderResult.Failed || sourceReader is null)
                {
                    return DiscoveryResults.Failed<IReadOnlyList<NativeVideoCapability>>(new DiscoveryFailure(DiscoveryOperation.NativeMediaTypeDiscovery, createReaderResult.Value, "Could not inspect the selected device formats."));
                }

                var capabilities = new List<NativeVideoCapability>();
                for (var mediaTypeIndex = 0; ; mediaTypeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IMFMediaType? mediaType = null;
                    try
                    {
                        sourceReader.GetNativeMediaType(unchecked((uint)(int)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM), (uint)mediaTypeIndex, out mediaType);
                        if (mediaType is null)
                        {
                            break;
                        }

                        capabilities.Add(CreateCapability(mediaType, mediaTypeIndex));
                    }
                    catch (COMException)
                    {
                        break;
                    }
                    finally
                    {
                        if (mediaType is not null && Marshal.IsComObject(mediaType)) Marshal.ReleaseComObject(mediaType);
                    }
                }

                return DiscoveryResults.Success(NativeVideoCapabilityFormatter.SortAndDeduplicate(capabilities));
            }
            finally
            {
                if (sourceReader is not null && Marshal.IsComObject(sourceReader)) Marshal.ReleaseComObject(sourceReader);
                if (mediaSource is not null)
                {
                    mediaSource.Shutdown();
                    if (Marshal.IsComObject(mediaSource)) Marshal.ReleaseComObject(mediaSource);
                }
                if (Marshal.IsComObject(attributes)) Marshal.ReleaseComObject(attributes);
            }
        }
        finally
        {
            PInvoke.CoUninitialize();
        }
    }

    private static NativeVideoCapability CreateCapability(IMFMediaType mediaType, int mediaTypeIndex)
    {
        mediaType.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
        mediaType.GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out var frameSize);
        mediaType.GetUINT64(PInvoke.MF_MT_FRAME_RATE, out var frameRate);
        uint interlaceValue = 0;
        try { mediaType.GetUINT32(PInvoke.MF_MT_INTERLACE_MODE, out interlaceValue); } catch (COMException) { }
        ulong pixelAspectRatio = 0;
        try { mediaType.GetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, out pixelAspectRatio); } catch (COMException) { }

        var width = (uint)(frameSize >> 32);
        var height = (uint)frameSize;
        var numerator = (uint)(frameRate >> 32);
        var denominator = (uint)frameRate;
        var aspectNumerator = (uint)(pixelAspectRatio >> 32);
        var aspectDenominator = (uint)pixelAspectRatio;
        var interlace = interlaceValue == 2 ? VideoInterlaceMode.Progressive : interlaceValue >= 3 ? VideoInterlaceMode.Interlaced : VideoInterlaceMode.Unknown;
        var subtypeLabel = GetSubtypeLabel(subtype);

        return new NativeVideoCapability(
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
    }

    private static string GetSubtypeLabel(Guid subtype)
    {
        if (subtype == PInvoke.MFVideoFormat_MJPG) return "MJPEG";
        if (subtype == PInvoke.MFVideoFormat_YUY2) return "YUY2";
        if (subtype == PInvoke.MFVideoFormat_NV12) return "NV12";
        if (subtype == PInvoke.MFVideoFormat_RGB32) return "RGB32";
        if (subtype == PInvoke.MFVideoFormat_RGB24) return "RGB24";
        if (subtype == PInvoke.MFVideoFormat_H264) return "H.264";
        return "Other";
    }
}
