using System.Diagnostics;
using System.Runtime.InteropServices;
using HdmiCaptureCardMonitor.Capture.Diagnostics;
using HdmiCaptureCardMonitor.Capture.Interop;
using HdmiCaptureCardMonitor.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;

namespace HdmiCaptureCardMonitor.Capture.Rendering;

internal sealed class D3D11PreviewRenderer : IDisposable
{
    private const int DxgiErrorWasStillDrawing = unchecked((int)0x887A000A);
    private static readonly float[] Black = [0f, 0f, 0f, 1f];
    private static readonly D3D_FEATURE_LEVEL[] FeatureLevels =
    [
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1,
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0,
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_1,
        D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_10_0
    ];

    private readonly NativeVideoCapability sourceFormat;
    private readonly ResizeRequestMailbox resizeRequests = new();
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11VideoDevice videoDevice;
    private readonly ID3D11VideoContext videoContext;
    private readonly IDXGIDevice dxgiDevice;
    private readonly IDXGIAdapter adapter;
    private readonly IDXGIFactory2 factory;
    private readonly IDXGISwapChain1 swapChain;
    private readonly IDXGISwapChain2? swapChain2;
    private ID3D11VideoProcessorEnumerator? processorEnumerator;
    private ID3D11VideoProcessor? processor;
    private ID3D11Texture2D? backBuffer;
    private ID3D11VideoProcessorOutputView? outputView;
    private ID3D11RenderTargetView? renderTargetView;
    private PreviewSurfaceSize outputSize;
    private bool disposed;

    private D3D11PreviewRenderer(
        NativeVideoCapability sourceFormat,
        PreviewSurfaceSize initialSize,
        nint targetWindow,
        PreviewDriverType driverType,
        ID3D11Device device,
        ID3D11DeviceContext context)
    {
        this.sourceFormat = sourceFormat;
        DriverType = driverType;
        this.device = device;
        this.context = context;
        videoDevice = device as ID3D11VideoDevice ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics device does not support video processing.");
        videoContext = context as ID3D11VideoContext ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics context does not support video processing.");
        dxgiDevice = device as IDXGIDevice ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics device does not expose DXGI.");
        dxgiDevice.GetAdapter(out adapter);

        unsafe
        {
            var factoryId = typeof(IDXGIFactory2).GUID;
            adapter.GetParent(&factoryId, out var factoryObject);
            factory = (IDXGIFactory2)factoryObject;

            var size = initialSize.IsEmpty ? new PreviewSurfaceSize(1, 1) : initialSize;
            var description = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)size.PixelWidth,
                Height = (uint)size.PixelHeight,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Stereo = false,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT
            };
            factory.CreateSwapChainForHwnd(device, new HWND((void*)targetWindow), &description, null, null!, out swapChain);
        }

        swapChain2 = swapChain as IDXGISwapChain2;
        swapChain2?.SetMaximumFrameLatency(1);
        RecreateOutputResources(initialSize);

        var managerResult = PInvoke.MFCreateDXGIDeviceManager(out var resetToken, out var manager);
        if (managerResult.Failed) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The Media Foundation graphics-device manager could not be created.", managerResult.Value);
        DeviceManager = manager;
        DeviceManager.ResetDevice(device, resetToken);
    }

    public PreviewDriverType DriverType { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }
    public static int ApplicationFrameQueueCapacity => 0;

    public static D3D11PreviewRenderer Create(NativeVideoCapability sourceFormat, PreviewSurfaceSize initialSize, nint targetWindow)
    {
        if (targetWindow == 0) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The preview surface is unavailable.");
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        var hardware = TryCreateDevice(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, flags);
        if (hardware is not null) return new D3D11PreviewRenderer(sourceFormat, initialSize, targetWindow, PreviewDriverType.Hardware, hardware.Value.Device, hardware.Value.Context);

        var warp = TryCreateDevice(D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, flags);
        if (warp is not null) return new D3D11PreviewRenderer(sourceFormat, initialSize, targetWindow, PreviewDriverType.Warp, warp.Value.Device, warp.Value.Context);
        throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "Windows could not create a Direct3D 11 video device.");
    }

    public void RequestResize(PreviewSurfaceSize size) => resizeRequests.Post(size);

    public unsafe bool Render(IMFDXGIBuffer dxgiBuffer, PreviewDiagnosticsTracker diagnostics)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resizeRequests.TryTake(out var requestedSize)) RecreateOutputResources(requestedSize);
        if (outputSize.IsEmpty || processorEnumerator is null || processor is null || outputView is null || renderTargetView is null) return false;

        void* resourcePointer = null;
        ID3D11Texture2D? inputTexture = null;
        ID3D11VideoProcessorInputView? inputView = null;
        try
        {
            var textureId = typeof(ID3D11Texture2D).GUID;
            dxgiBuffer.GetResource(&textureId, &resourcePointer);
            if (resourcePointer is null) throw CreateFailure(PreviewFailureCategory.UnsupportedGpuBuffer, "The decoded frame did not expose a Direct3D texture.");
            inputTexture = WrapComPointer<ID3D11Texture2D>(resourcePointer);
            Marshal.Release((nint)resourcePointer);
            resourcePointer = null;
            dxgiBuffer.GetSubresourceIndex(out var subresourceIndex);

            var inputDescription = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
            {
                FourCC = 0,
                ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D
            };
            inputDescription.Texture2D.MipSlice = 0;
            inputDescription.Texture2D.ArraySlice = subresourceIndex;
            ID3D11VideoProcessorInputView_unmanaged* rawInputView = null;
            videoDevice.CreateVideoProcessorInputView((ID3D11Resource)inputTexture, processorEnumerator, &inputDescription, &rawInputView);
            if (rawInputView is null) throw CreateFailure(PreviewFailureCategory.UnsupportedGpuBuffer, "The decoded frame could not be bound to the GPU video processor.");
            inputView = WrapComPointer<ID3D11VideoProcessorInputView>(rawInputView);
            rawInputView->Release();

            context.ClearRenderTargetView(renderTargetView, Black);
            var sourceWidth = checked((int)sourceFormat.Width);
            var sourceHeight = checked((int)sourceFormat.Height);
            var destination = FitRectangleCalculator.Calculate(sourceWidth, sourceHeight, outputSize.PixelWidth, outputSize.PixelHeight);
            var sourceRectangle = new RECT(0, 0, sourceWidth, sourceHeight);
            var destinationRectangle = new RECT(destination.X, destination.Y, destination.Right, destination.Bottom);
            videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, &sourceRectangle);
            videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, &destinationRectangle);
            videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);

            var stream = new D3D11_VIDEO_PROCESSOR_STREAM
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                pInputSurface = inputView
            };
            videoContext.VideoProcessorBlt(processor, outputView, 0, 1, [stream]);
            var presentResult = swapChain.Present(0, DXGI_PRESENT.DXGI_PRESENT_DO_NOT_WAIT);
            if (presentResult.Value == DxgiErrorWasStillDrawing)
            {
                diagnostics.RecordPresentationFailure();
                return false;
            }
            if (presentResult.Failed)
            {
                diagnostics.RecordPresentationFailure();
                throw CreateFailure(MapPresentationFailure(presentResult.Value), "The preview frame could not be presented.", presentResult.Value);
            }

            return true;
        }
        catch (COMException exception)
        {
            throw CreateFailure(MapPresentationFailure(exception.HResult), "The GPU preview pipeline failed while rendering a frame.", exception.HResult, exception);
        }
        finally
        {
            if (resourcePointer is not null) Marshal.Release((nint)resourcePointer);
            ReleaseComObject(inputView);
            ReleaseComObject(inputTexture);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ReleaseOutputResources();
        ReleaseComObject(DeviceManager);
        ReleaseComObject(swapChain);
        ReleaseComObject(factory);
        ReleaseComObject(adapter);
        ReleaseComObject(context);
        ReleaseComObject(device);
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context)? TryCreateDevice(D3D_DRIVER_TYPE driverType, D3D11_CREATE_DEVICE_FLAG flags)
    {
        var result = PInvoke.D3D11CreateDevice(null!, driverType, default, flags, FeatureLevels, PInvoke.D3D11_SDK_VERSION, out var device, out _, out var context);
        return result.Failed ? null : (device, context);
    }

    private unsafe void RecreateOutputResources(PreviewSurfaceSize size)
    {
        outputSize = size;
        if (size.IsEmpty) return;

        ReleaseOutputResources();
        swapChain.ResizeBuffers(
            2,
            (uint)size.PixelWidth,
            (uint)size.PixelHeight,
            DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);

        var textureId = typeof(ID3D11Texture2D).GUID;
        swapChain.GetBuffer(0, &textureId, out var bufferObject);
        backBuffer = (ID3D11Texture2D)bufferObject;

        var contentDescription = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = sourceFormat.InterlaceMode == VideoInterlaceMode.Interlaced
                ? D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_INTERLACED_TOP_FIELD_FIRST
                : D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DXGI_RATIONAL { Numerator = sourceFormat.FrameRateNumerator, Denominator = sourceFormat.FrameRateDenominator },
            InputWidth = (uint)sourceFormat.Width,
            InputHeight = (uint)sourceFormat.Height,
            OutputFrameRate = new DXGI_RATIONAL { Numerator = sourceFormat.FrameRateNumerator, Denominator = sourceFormat.FrameRateDenominator },
            OutputWidth = (uint)size.PixelWidth,
            OutputHeight = (uint)size.PixelHeight,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED
        };
        videoDevice.CreateVideoProcessorEnumerator(&contentDescription, out processorEnumerator);
        videoDevice.CreateVideoProcessor(processorEnumerator, 0, out processor);
        videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, contentDescription.InputFrameFormat);

        var outputDescription = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
        {
            ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D
        };
        outputDescription.Texture2D.MipSlice = 0;
        ID3D11VideoProcessorOutputView_unmanaged* rawOutputView = null;
        videoDevice.CreateVideoProcessorOutputView((ID3D11Resource)backBuffer, processorEnumerator, &outputDescription, &rawOutputView);
        if (rawOutputView is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The swap-chain video output could not be created.");
        outputView = WrapComPointer<ID3D11VideoProcessorOutputView>(rawOutputView);
        rawOutputView->Release();

        ID3D11RenderTargetView_unmanaged* rawRenderTarget = null;
        device.CreateRenderTargetView((ID3D11Resource)backBuffer, null, &rawRenderTarget);
        if (rawRenderTarget is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The swap-chain render target could not be created.");
        renderTargetView = WrapComPointer<ID3D11RenderTargetView>(rawRenderTarget);
        rawRenderTarget->Release();
    }

    private void ReleaseOutputResources()
    {
        ReleaseComObject(renderTargetView);
        renderTargetView = null;
        ReleaseComObject(outputView);
        outputView = null;
        ReleaseComObject(backBuffer);
        backBuffer = null;
        ReleaseComObject(processor);
        processor = null;
        ReleaseComObject(processorEnumerator);
        processorEnumerator = null;
        context.Flush();
    }

    private static unsafe T WrapComPointer<T>(void* pointer) where T : class => (T)Marshal.GetObjectForIUnknown((nint)pointer);

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static PreviewNativeException CreateFailure(PreviewFailureCategory category, string message, int? hresult = null, Exception? exception = null) =>
        new(new PreviewFailure(category, message, hresult, exception));

    private static PreviewFailureCategory MapPresentationFailure(int hresult) => hresult switch
    {
        unchecked((int)0x887A0005) or unchecked((int)0x887A0007) => PreviewFailureCategory.DeviceRemoved,
        _ => PreviewFailureCategory.PresentationFailure
    };
}
