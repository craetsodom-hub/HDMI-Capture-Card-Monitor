using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Capture.Audio;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Presentation;
using HdmiCaptureCardMonitor.Presentation.Fullscreen;
using HdmiCaptureCardMonitor.ViewModels;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace HdmiCaptureCardMonitor;

public partial class MainWindow : Window, IDisposable
{
    private readonly IApplicationLogger logger;
    private readonly MainWindowViewModel viewModel;
    private readonly FullscreenWindowController fullscreenController;
    private readonly FullscreenCursorController fullscreenCursorController;
    private readonly WindowCloseCoordinator closeCoordinator = new();
    private IInputElement? focusBeforeDialog;
    private bool isDisposed;

    public MainWindow(
        IApplicationLogger logger,
        ICaptureDeviceDiscoveryService discoveryService,
        ICapturePreviewService previewService,
        IAudioEndpointDiscoveryService audioDiscoveryService,
        IAudioMonitorService audioMonitorService,
        string? startupNotice = null)
    {
        this.logger = logger;
        InitializeComponent();
        ClampInitialSizeToWorkArea();
        var nativeApi = new WindowNativeApi();
        fullscreenController = new FullscreenWindowController(new WpfFullscreenWindowAdapter(this, nativeApi, ApplyNativeTitleBarTheme));
        fullscreenCursorController = new FullscreenCursorController(new DispatcherFullscreenInactivityTimer(Dispatcher), PreviewHost);
        viewModel = new MainWindowViewModel(
            logger,
            startupNotice,
            discoveryService: discoveryService,
            previewService: previewService,
            previewSurface: PreviewHost,
            fullscreenController: fullscreenController,
            audioDiscoveryService: audioDiscoveryService,
            audioMonitorService: audioMonitorService);
        DataContext = viewModel;
        viewModel.PropertyChanging += OnViewModelPropertyChanging;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        SizeChanged += OnWindowSizeChanged;
        StateChanged += (_, _) => PreviewHost.SetWindowMinimized(WindowState == WindowState.Minimized);
        PreviewHost.PointerActivity += OnPreviewPointerActivity;
        MouseMove += OnWindowPointerActivity;
        fullscreenController.StateChanged += OnFullscreenStateChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private unsafe void OnSourceInitialized(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        ApplyNativeTitleBarTheme();
    }

    private unsafe void ApplyNativeTitleBarTheme()
    {

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == 0) return;
            BOOL useDarkMode = ApplicationThemeManager.CurrentTheme == ApplicationTheme.Dark;
            _ = PInvoke.DwmSetWindowAttribute(
                new HWND((void*)handle),
                DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
                &useDarkMode,
                (uint)sizeof(BOOL));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Native title-bar theming is optional and must never prevent startup.
        }
    }

    private void ClampInitialSizeToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyResponsiveLayout(ActualWidth);
        viewModel.StartInitialDiscovery();
    }

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(MainWindowViewModel.IsInformationDialogOpen) && !viewModel.IsInformationDialogOpen)
        {
            focusBeforeDialog = Keyboard.FocusedElement;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName == nameof(MainWindowViewModel.SessionState) && viewModel.SessionState != Models.CaptureSessionState.Previewing)
            fullscreenCursorController.RestoreForFailure();
        if (e.PropertyName != nameof(MainWindowViewModel.IsInformationDialogOpen)) return;

        var dialogOpen = viewModel.IsInformationDialogOpen;
        if (dialogOpen)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => DialogPrimaryButton.Focus()));
            return;
        }

        var requestedFocus = focusBeforeDialog;
        focusBeforeDialog = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RestoreMainContentFocus(requestedFocus)));
    }

    private void RestoreMainContentFocus(IInputElement? requestedFocus)
    {
        if (TryFocus(requestedFocus)) return;
        if (TryFocus(DeviceSelector)) return;
        if (TryFocus(StartStopButton)) return;
        _ = TryFocus(SettingsButton);
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _ = sender;
        try
        {
            if (e.Key == Key.F11)
            {
                e.Handled = true;
                if (viewModel.ToggleFullscreenCommand.CanExecute(null))
                    await viewModel.ToggleFullscreenCommand.ExecuteAsync(null);
                return;
            }

            if (e.Key != Key.Escape) return;
            if (viewModel.IsFullscreen || viewModel.IsFullscreenTransitioning)
            {
                e.Handled = true;
                await viewModel.ExitFullscreenForWindowAsync(FullscreenExitReason.Escape);
                return;
            }

            if (viewModel.IsInformationDialogOpen)
            {
                e.Handled = true;
                viewModel.CloseInformationDialogCommand.Execute(null);
            }
        }
        catch (Exception exception)
        {
            try { logger.LogError("A keyboard fullscreen request failed safely.", exception); }
            catch { }
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (!viewModel.IsFullscreenPresentation) ApplyResponsiveLayout(ActualWidth);
    }

    private void OnPreviewPointerActivity(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        fullscreenCursorController.NotifyPointerActivity();
    }

    private void OnWindowPointerActivity(object sender, MouseEventArgs e)
    {
        _ = sender;
        _ = e;
        fullscreenCursorController.NotifyPointerActivity();
    }

    private void OnFullscreenStateChanged(object? sender, FullscreenControllerStateChangedEventArgs e)
    {
        _ = sender;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnFullscreenStateChanged(sender, e));
            return;
        }

        fullscreenCursorController.SetTransitioning(e.IsTransitioning);
        if (e.IsFullscreen && !e.IsTransitioning && viewModel.SessionState == Models.CaptureSessionState.Previewing)
            fullscreenCursorController.EnterFullscreenPreview();
        else if (!e.IsFullscreen)
            fullscreenCursorController.ExitFullscreen();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        if (!FullscreenDisplayChangePolicy.ShouldRequestExit(
                isDisposed,
                fullscreenController.IsFullscreen,
                fullscreenController.IsTransitioning)) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (!FullscreenDisplayChangePolicy.ShouldRequestExit(
                    isDisposed,
                    fullscreenController.IsFullscreen,
                    fullscreenController.IsTransitioning)) return;
            fullscreenCursorController.RestoreForFailure();
            _ = ExitFullscreenAfterDisplayChangeAsync();
        }));
    }

    private async Task ExitFullscreenAfterDisplayChangeAsync()
    {
        try
        {
            await viewModel.ExitFullscreenForWindowAsync(FullscreenExitReason.DisplayRemoved);
        }
        catch (Exception exception)
        {
            fullscreenCursorController.RestoreForFailure();
            SafeLogError("A display-change fullscreen exit failed safely.", exception);
        }
    }

    private static bool TryFocus(IInputElement? candidate) => candidate switch
    {
        UIElement element when element.IsEnabled && element.IsVisible && element.Focusable => element.Focus(),
        ContentElement element when element.IsEnabled && element.Focusable => element.Focus(),
        _ => false
    };

    private void ApplyResponsiveLayout(double windowWidth)
    {
        var stacked = ResponsiveLayoutPolicy.UsesStackedSelectors(windowWidth);
        DeviceColumn.Width = LayoutMetrics.StarColumn;
        ConfigurationGapColumn.Width = stacked ? LayoutMetrics.CollapsedColumn : LayoutMetrics.WideControlGapColumn;
        FormatColumn.Width = stacked ? LayoutMetrics.CollapsedColumn : LayoutMetrics.StarColumn;

        Grid.SetColumn(FormatPanel, stacked ? 0 : 2);
        Grid.SetRow(FormatPanel, stacked ? 1 : 0);
        FormatPanel.Margin = stacked ? LayoutMetrics.StackedSectionMargin : LayoutMetrics.NoMargin;

        AudioInputColumn.Width = LayoutMetrics.StarColumn;
        AudioInputGapColumn.Width = stacked ? LayoutMetrics.CollapsedColumn : LayoutMetrics.WideControlGapColumn;
        AudioOutputColumn.Width = stacked ? LayoutMetrics.CollapsedColumn : LayoutMetrics.StarColumn;
        AudioOutputGapColumn.Width = stacked ? LayoutMetrics.CollapsedColumn : LayoutMetrics.WideControlGapColumn;
        AudioControlColumn.Width = stacked ? LayoutMetrics.CollapsedColumn : LayoutMetrics.StarColumn;
        Grid.SetColumn(AudioOutputPanel, stacked ? 0 : 2);
        Grid.SetRow(AudioOutputPanel, stacked ? 1 : 0);
        AudioOutputPanel.Margin = stacked ? LayoutMetrics.StackedSectionMargin : LayoutMetrics.NoMargin;
        Grid.SetColumn(AudioControlPanel, stacked ? 0 : 4);
        Grid.SetRow(AudioControlPanel, stacked ? 2 : 0);
        AudioControlPanel.Margin = stacked ? LayoutMetrics.StackedSectionMargin : LayoutMetrics.NoMargin;

        Grid.SetColumn(ActionButtons, stacked ? 0 : 1);
        Grid.SetRow(ActionButtons, stacked ? 1 : 0);
        ActionButtons.Margin = stacked ? LayoutMetrics.StackedSectionMargin : LayoutMetrics.NoMargin;
        ActionButtons.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        var decision = closeCoordinator.Evaluate(
            fullscreenController.IsFullscreen,
            fullscreenController.IsTransitioning);
        if (decision == WindowCloseDecision.AllowAndDispose)
        {
            Dispose();
            return;
        }

        e.Cancel = true;
        if (decision == WindowCloseDecision.CancelWhilePreparing) return;
        viewModel.SetWindowClosing();
        fullscreenCursorController.ExitFullscreen();
        _ = PrepareFullscreenCloseAsync();
    }

    private async Task PrepareFullscreenCloseAsync()
    {
        try
        {
            await viewModel.ExitFullscreenForWindowAsync(FullscreenExitReason.Closing);
        }
        catch (Exception exception)
        {
            SafeLogError("Fullscreen close preparation failed safely.", exception);
        }
        finally
        {
            if (closeCoordinator.CompletePreparationAndRequestClose())
            {
                try { _ = Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Close)); }
                catch (Exception exception) when (exception is InvalidOperationException or TaskCanceledException)
                {
                    SafeLogError("The prepared window close could not be reissued because shutdown was already underway.", exception);
                }
            }
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        Dispose();
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        closeCoordinator.MarkDisposed();
        viewModel.PropertyChanging -= OnViewModelPropertyChanging;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        SizeChanged -= OnWindowSizeChanged;
        PreviewHost.PointerActivity -= OnPreviewPointerActivity;
        MouseMove -= OnWindowPointerActivity;
        fullscreenController.StateChanged -= OnFullscreenStateChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        fullscreenCursorController.Dispose();
        viewModel.Dispose();
        var controllerDisposal = fullscreenController.DisposeAsync();
        if (controllerDisposal.IsCompletedSuccessfully)
            controllerDisposal.GetAwaiter().GetResult();
        else
            _ = ObserveControllerDisposalAsync(controllerDisposal);
        GC.SuppressFinalize(this);
    }

    private async Task ObserveControllerDisposalAsync(ValueTask disposal)
    {
        try { await disposal; }
        catch (Exception exception) { SafeLogError("Fullscreen controller disposal failed safely.", exception); }
    }

    private void SafeLogError(string message, Exception exception)
    {
        try { logger.LogError(message, exception); }
        catch
        {
            // Logging must not interfere with display recovery or window shutdown.
        }
    }
}
