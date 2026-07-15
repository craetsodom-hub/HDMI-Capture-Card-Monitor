using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Capture.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace HdmiCaptureCardMonitor.Capture.Audio;

internal sealed unsafe class CoreAudioEndpointDiscoveryService(IApplicationLogger logger) : IAudioEndpointDiscoveryService
{
    private readonly ActiveOperationRegistry operations = new();

    public bool WorkersSettled => operations.WorkersSettled;

    public Task<AudioEndpointDiscoveryResult> EnumerateActiveEndpointsAsync(CancellationToken cancellationToken = default)
    {
        var task = operations.TryStart(Enumerate, cancellationToken);
        return task ?? Task.FromResult(AudioEndpointDiscoveryResult.Failed(
            new AudioMonitorFailure(AudioMonitorFailureCategory.OtherFailure, "Audio endpoint discovery is shutting down.")));
    }

    public void Dispose()
    {
        operations.Dispose();
        if (!operations.WorkersSettled) SafeWarning("Core Audio discovery did not settle within the three-second shutdown bound.");
    }

    private AudioEndpointDiscoveryResult Enumerate(CancellationToken cancellationToken)
    {
        var apartment = PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        if (apartment.Failed)
            return Failed(AudioMonitorFailureCategory.EndpointCreateFailed, apartment.Value, "Windows could not initialize audio endpoint discovery.");

        IMMDeviceEnumerator? enumerator = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            var captures = EnumerateFlow(enumerator, EDataFlow.eCapture, AudioEndpointDataFlow.Capture, cancellationToken);
            var renders = EnumerateFlow(enumerator, EDataFlow.eRender, AudioEndpointDataFlow.Render, cancellationToken);
            var defaultId = ReadDefaultRenderId(enumerator);
            var normalizedCaptures = Disambiguate(captures);
            var normalizedRenders = Disambiguate(renders);
            var defaultRender = defaultId is null ? null : normalizedRenders.FirstOrDefault(endpoint => endpoint.Id == defaultId);
            return AudioEndpointDiscoveryResult.Succeeded(normalizedCaptures, normalizedRenders, defaultRender);
        }
        catch (OperationCanceledException)
        {
            return AudioEndpointDiscoveryResult.Failed(
                new AudioMonitorFailure(AudioMonitorFailureCategory.OtherFailure, "Audio endpoint discovery was cancelled."));
        }
        catch (COMException exception)
        {
            return Failed(MapFailure(exception.HResult), exception.HResult, CustomerMessage(MapFailure(exception.HResult)), exception);
        }
        finally
        {
            ReleaseComObject(enumerator);
            PInvoke.CoUninitialize();
        }
    }

    private List<AudioEndpoint> EnumerateFlow(
        IMMDeviceEnumerator enumerator,
        EDataFlow nativeFlow,
        AudioEndpointDataFlow managedFlow,
        CancellationToken cancellationToken)
    {
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator.EnumAudioEndpoints(nativeFlow, DEVICE_STATE.DEVICE_STATE_ACTIVE, out collection);
            collection.GetCount(out var count);
            var endpoints = new List<AudioEndpoint>(checked((int)count));
            for (var index = 0u; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMMDevice? device = null;
                try
                {
                    collection.Item(index, out device);
                    var endpoint = ReadEndpoint(device, managedFlow);
                    if (endpoint is not null) endpoints.Add(endpoint);
                }
                catch (COMException exception)
                {
                    SafeWarning($"One malformed active audio {managedFlow.ToString().ToLowerInvariant()} endpoint was ignored (0x{exception.HResult:X8}).");
                }
                finally { ReleaseComObject(device); }
            }
            return endpoints;
        }
        finally { ReleaseComObject(collection); }
    }

    private static unsafe AudioEndpoint? ReadEndpoint(IMMDevice device, AudioEndpointDataFlow flow)
    {
        device.GetState(out var state);
        if ((state & DEVICE_STATE.DEVICE_STATE_ACTIVE) == 0) return null;
        device.GetId(out var nativeId);
        try
        {
            var id = nativeId.ToString();
            if (string.IsNullOrWhiteSpace(id)) return null;
            IPropertyStore? properties = null;
            try
            {
                device.OpenPropertyStore(STGM.STGM_READ, out properties);
                return new AudioEndpoint(
                    id,
                    ReadString(properties, PInvoke.PKEY_Device_FriendlyName),
                    flow,
                    ReadString(properties, PInvoke.PKEY_Device_DeviceDesc),
                    ReadString(properties, PInvoke.PKEY_DeviceInterface_FriendlyName),
                    ReadGuid(properties, PInvoke.PKEY_Device_ContainerId));
            }
            finally { ReleaseComObject(properties); }
        }
        finally { if (nativeId.Value is not null) PInvoke.CoTaskMemFree(nativeId); }
    }

    private static string? ReadDefaultRenderId(IMMDeviceEnumerator enumerator)
    {
        IMMDevice? device = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device);
            device.GetId(out var nativeId);
            try { return nativeId.ToString(); }
            finally { if (nativeId.Value is not null) PInvoke.CoTaskMemFree(nativeId); }
        }
        catch (COMException) { return null; }
        finally { ReleaseComObject(device); }
    }

    private static string? ReadString(IPropertyStore properties, PROPERTYKEY key)
    {
        PROPVARIANT value = default;
        try
        {
            properties.GetValue(key, out value);
            return value.vt == VARENUM.VT_LPWSTR ? value.pwszVal.ToString() : null;
        }
        catch (COMException) { return null; }
        finally { _ = PInvoke.PropVariantClear(ref value); }
    }

    private static unsafe Guid? ReadGuid(IPropertyStore properties, PROPERTYKEY key)
    {
        PROPVARIANT value = default;
        try
        {
            properties.GetValue(key, out value);
            return value.vt == VARENUM.VT_CLSID && value.puuid is not null ? *value.puuid : null;
        }
        catch (COMException) { return null; }
        finally { _ = PInvoke.PropVariantClear(ref value); }
    }

    internal static IReadOnlyList<AudioEndpoint> Disambiguate(IEnumerable<AudioEndpoint> source)
    {
        var ordered = source.OrderBy(endpoint => endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.Id, StringComparer.Ordinal).ToArray();
        var totals = ordered.GroupBy(endpoint => endpoint.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AudioEndpoint>(ordered.Length);
        foreach (var endpoint in ordered)
        {
            seen.TryGetValue(endpoint.DisplayName, out var ordinal);
            seen[endpoint.DisplayName] = ++ordinal;
            result.Add(totals[endpoint.DisplayName] == 1 ? endpoint : new AudioEndpoint(
                endpoint.Id,
                $"{endpoint.DisplayName} ({ordinal})",
                endpoint.DataFlow,
                endpoint.DeviceDescription,
                endpoint.InterfaceFriendlyName,
                endpoint.ContainerId));
        }
        return result;
    }

    internal static AudioMonitorFailureCategory MapFailure(int hresult) => hresult switch
    {
        unchecked((int)0x80070005) => AudioMonitorFailureCategory.AccessDenied,
        unchecked((int)0x88890004) => AudioMonitorFailureCategory.DeviceInvalidated,
        unchecked((int)0x88890010) => AudioMonitorFailureCategory.AudioServiceNotRunning,
        _ => AudioMonitorFailureCategory.EndpointCreateFailed
    };

    private static string CustomerMessage(AudioMonitorFailureCategory category) => category switch
    {
        AudioMonitorFailureCategory.AccessDenied => "Microphone access is blocked. Allow desktop apps to use the microphone in Windows Privacy & security.",
        AudioMonitorFailureCategory.AudioServiceNotRunning => "Windows Audio is not running.",
        _ => "Audio endpoints are unavailable."
    };

    private static AudioEndpointDiscoveryResult Failed(AudioMonitorFailureCategory category, int? hresult, string message, Exception? exception = null) =>
        AudioEndpointDiscoveryResult.Failed(new AudioMonitorFailure(category, message, hresult, exception));

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private void SafeWarning(string message)
    {
        try { logger.Warning(message); }
        catch (Exception) { }
    }
}
