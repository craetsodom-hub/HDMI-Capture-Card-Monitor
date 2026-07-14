using System.Runtime.InteropServices;
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
    private int disposed;

    private D3D11PreviewRenderer(
        NativeVideoCapability sourceFormat,
        PreviewDriverType driverType,
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext,
        IDXGIDevice dxgiDevice,
        IDXGIAdapter adapter,
        IDXGIFactory2 factory,
        IDXGISwapChain1 swapChain,
        IMFDXGIDeviceManager deviceManager)
    {
        this.sourceFormat = sourceFormat;
        DriverType = driverType;
        this.device = device;
        this.context = context;
        this.videoDevice = videoDevice;
        this.videoContext = videoContext;
        this.dxgiDevice = dxgiDevice;
        this.adapter = adapter;
        this.factory = factory;
        this.swapChain = swapChain;
        swapChain2 = swapChain as IDXGISwapChain2;
        DeviceManager = deviceManager;
    }

    public PreviewDriverType DriverType { get; }
    public IMFDXGIDeviceManager DeviceManager { get; }
    public static int ApplicationFrameQueueCapacity => 0;

    public static D3D11PreviewRenderer Create(NativeVideoCapability sourceFormat, PreviewSurfaceSize initialSize, nint targetWindow)
    {
        if (targetWindow == 0 || initialSize.IsEmpty)
            throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The preview surface is unavailable or has no drawable area.");

        PreviewNativeException? lastFailure = null;
        var hardware = TryCreateRenderer(sourceFormat, initialSize, targetWindow, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, PreviewDriverType.Hardware, out var hardwareFailure);
        if (hardware is not null) return hardware;
        lastFailure = hardwareFailure;

        var warp = TryCreateRenderer(sourceFormat, initialSize, targetWindow, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, PreviewDriverType.Warp, out var warpFailure);
        if (warp is not null) return warp;
        lastFailure = warpFailure ?? lastFailure;

        throw lastFailure ?? CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "Windows could not create a Direct3D 11 video device.");
    }

    public void RequestResize(PreviewSurfaceSize size) => resizeRequests.Post(size);

    public unsafe PreviewRenderOutcome Render(IMFDXGIBuffer dxgiBuffer)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (resizeRequests.TryTake(out var requestedSize)) RecreateOutputResources(requestedSize);
        if (outputSize.IsEmpty || processorEnumerator is null || processor is null || outputView is null || renderTargetView is null)
            return PreviewRenderOutcome.SurfaceUnavailable;

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
            try
            {
                videoDevice.CreateVideoProcessorInputView((ID3D11Resource)inputTexture, processorEnumerator, &inputDescription, &rawInputView);
                if (rawInputView is null) throw CreateFailure(PreviewFailureCategory.UnsupportedGpuBuffer, "The decoded frame could not be bound to the GPU video processor.");
                inputView = WrapComPointer<ID3D11VideoProcessorInputView>(rawInputView);
            }
            finally
            {
                if (rawInputView is not null) rawInputView->Release();
            }

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
            if (presentResult.Value == DxgiErrorWasStillDrawing) return PreviewRenderOutcome.WasStillDrawing;
            if (presentResult.Failed)
                throw CreateFailure(MapPresentationFailure(presentResult.Value), "The preview frame could not be presented.", presentResult.Value);

            return PreviewRenderOutcome.Presented;
        }
        catch (COMException exception)
        {
            throw CreateFailure(MapPresentationFailure(exception.HResult), "The GPU preview pipeline failed while rendering a frame.", exception.HResult, exception);
        }
        finally
        {
            if (resourcePointer is not null) Marshal.Release((nint)resourcePointer);
            SafeRelease(inputView);
            SafeRelease(inputTexture);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ReleaseOutputResources();
        SafeRelease(DeviceManager);
        SafeRelease(swapChain);
        SafeRelease(factory);
        SafeRelease(adapter);
        SafeRelease(context);
        SafeRelease(device);
    }

    private static D3D11PreviewRenderer? TryCreateRenderer(
        NativeVideoCapability sourceFormat,
        PreviewSurfaceSize initialSize,
        nint targetWindow,
        D3D_DRIVER_TYPE nativeDriverType,
        PreviewDriverType diagnosticDriverType,
        out PreviewNativeException? failure)
    {
        failure = null;
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT;
        var result = PInvoke.D3D11CreateDevice(null!, nativeDriverType, default, flags, FeatureLevels, PInvoke.D3D11_SDK_VERSION, out var device, out _, out var context);
        if (result.Failed || device is null || context is null)
        {
            SafeRelease(context);
            SafeRelease(device);
            return null;
        }

        using var ownership = new NativeConstructionScope();
        ownership.Own(device, SafeRelease);
        ownership.Own(context, SafeRelease);

        try
        {
            var videoDevice = device as ID3D11VideoDevice ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics device does not support video processing.");
            var videoContext = context as ID3D11VideoContext ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics context does not support video processing.");
            var dxgiDevice = device as IDXGIDevice ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics device does not expose DXGI.");
            dxgiDevice.GetAdapter(out var adapter);
            if (adapter is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The graphics adapter could not be resolved.");
            ownership.Own(adapter, SafeRelease);

            IDXGIFactory2 factory;
            unsafe
            {
                var factoryId = typeof(IDXGIFactory2).GUID;
                adapter.GetParent(&factoryId, out var factoryObject);
                if (factoryObject is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The DXGI factory could not be resolved.");
                ownership.Own(factoryObject, SafeRelease);
                factory = factoryObject as IDXGIFactory2 ?? throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The DXGI factory could not be resolved.");
            }

            IDXGISwapChain1 swapChain;
            unsafe
            {
                var description = new DXGI_SWAP_CHAIN_DESC1
                {
                    Width = (uint)initialSize.PixelWidth,
                    Height = (uint)initialSize.PixelHeight,
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
            if (swapChain is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The preview swap chain was not created.");
            ownership.Own(swapChain, SafeRelease);
            (swapChain as IDXGISwapChain2)?.SetMaximumFrameLatency(1);

            var managerResult = PInvoke.MFCreateDXGIDeviceManager(out var resetToken, out var manager);
            if (managerResult.Failed || manager is null)
                throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The Media Foundation graphics-device manager could not be created.", managerResult.Value);
            ownership.Own(manager, SafeRelease);
            manager.ResetDevice(device, resetToken);

            var renderer = new D3D11PreviewRenderer(
                sourceFormat,
                diagnosticDriverType,
                device,
                context,
                videoDevice,
                videoContext,
                dxgiDevice,
                adapter,
                factory,
                swapChain,
                manager);
            ownership.Commit();
            try
            {
                renderer.RecreateOutputResources(initialSize);
                return renderer;
            }
            catch
            {
                renderer.Dispose();
                throw;
            }
        }
        catch (PreviewNativeException exception)
        {
            failure = exception;
            return null;
        }
        catch (COMException exception)
        {
            failure = CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The Direct3D preview pipeline could not be constructed.", exception.HResult, exception);
            return null;
        }
        catch (Exception exception)
        {
            failure = CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The Direct3D preview pipeline could not be constructed.", null, exception);
            return null;
        }
    }

    private unsafe void RecreateOutputResources(PreviewSurfaceSize size)
    {
        outputSize = size;
        ReleaseOutputResources();
        if (size.IsEmpty) return;

        swapChain.ResizeBuffers(2, (uint)size.PixelWidth, (uint)size.PixelHeight, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_SWAP_CHAIN_FLAG.DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);

        var textureId = typeof(ID3D11Texture2D).GUID;
        swapChain.GetBuffer(0, &textureId, out var bufferObject);
        if (bufferObject is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The swap-chain back buffer was not created.");
        backBuffer = bufferObject as ID3D11Texture2D;
        if (backBuffer is null)
        {
            SafeRelease(bufferObject);
            throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The swap-chain back buffer was not created.");
        }

        var contentDescription = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = sourceFormat.InterlaceMode == VideoInterlaceMode.Interlaced
                ? D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_INTERLACED_TOP_FIELD_FIRST
                : D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DXGI_RATIONAL { Numerator = sourceFormat.FrameRateNumerator, Denominator = sourceFormat.FrameRateDenominator },
            InputWidth = sourceFormat.Width,
            InputHeight = sourceFormat.Height,
            OutputFrameRate = new DXGI_RATIONAL { Numerator = sourceFormat.FrameRateNumerator, Denominator = sourceFormat.FrameRateDenominator },
            OutputWidth = (uint)size.PixelWidth,
            OutputHeight = (uint)size.PixelHeight,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED
        };
        videoDevice.CreateVideoProcessorEnumerator(&contentDescription, out processorEnumerator);
        if (processorEnumerator is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The video-processor enumerator was not created.");
        videoDevice.CreateVideoProcessor(processorEnumerator, 0, out processor);
        if (processor is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The video processor was not created.");
        videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, contentDescription.InputFrameFormat);

        var outputDescription = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC { ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D };
        outputDescription.Texture2D.MipSlice = 0;
        ID3D11VideoProcessorOutputView_unmanaged* rawOutputView = null;
        try
        {
            videoDevice.CreateVideoProcessorOutputView((ID3D11Resource)backBuffer, processorEnumerator, &outputDescription, &rawOutputView);
            if (rawOutputView is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The swap-chain video output could not be created.");
            outputView = WrapComPointer<ID3D11VideoProcessorOutputView>(rawOutputView);
        }
        finally
        {
            if (rawOutputView is not null) rawOutputView->Release();
        }

        ID3D11RenderTargetView_unmanaged* rawRenderTarget = null;
        try
        {
            device.CreateRenderTargetView((ID3D11Resource)backBuffer, null, &rawRenderTarget);
            if (rawRenderTarget is null) throw CreateFailure(PreviewFailureCategory.D3DInitializationFailure, "The swap-chain render target could not be created.");
            renderTargetView = WrapComPointer<ID3D11RenderTargetView>(rawRenderTarget);
        }
        finally
        {
            if (rawRenderTarget is not null) rawRenderTarget->Release();
        }
    }

    private void ReleaseOutputResources()
    {
        var currentRenderTarget = renderTargetView;
        renderTargetView = null;
        SafeRelease(currentRenderTarget);
        var currentOutputView = outputView;
        outputView = null;
        SafeRelease(currentOutputView);
        var currentBackBuffer = backBuffer;
        backBuffer = null;
        SafeRelease(currentBackBuffer);
        var currentProcessor = processor;
        processor = null;
        SafeRelease(currentProcessor);
        var currentEnumerator = processorEnumerator;
        processorEnumerator = null;
        SafeRelease(currentEnumerator);
        try { context.Flush(); }
        catch (COMException) { }
        catch (InvalidComObjectException) { }
    }

    private static unsafe T WrapComPointer<T>(void* pointer) where T : class => (T)Marshal.GetObjectForIUnknown((nint)pointer);

    private static void SafeRelease(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
        catch (InvalidComObjectException) { }
        catch (COMException) { }
    }

    private static PreviewNativeException CreateFailure(PreviewFailureCategory category, string message, int? hresult = null, Exception? exception = null) =>
        new(new PreviewFailure(category, message, hresult, exception));

    private static PreviewFailureCategory MapPresentationFailure(int hresult) => hresult switch
    {
        unchecked((int)0x887A0005) or unchecked((int)0x887A0007) => PreviewFailureCategory.DeviceRemoved,
        _ => PreviewFailureCategory.PresentationFailure
    };
}
