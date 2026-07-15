using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Presentation;
using HdmiCaptureCardMonitor.ViewModels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace HdmiCaptureCardMonitor;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private IInputElement? focusBeforeDialog;
    private bool isDisposed;

    public MainWindow(IApplicationLogger logger, ICaptureDeviceDiscoveryService discoveryService, ICapturePreviewService previewService, string? startupNotice = null)
    {
        InitializeComponent();
        ClampInitialSizeToWorkArea();
        viewModel = new MainWindowViewModel(logger, startupNotice, discoveryService: discoveryService, previewService: previewService, previewSurface: PreviewHost);
        DataContext = viewModel;
        viewModel.PropertyChanging += OnViewModelPropertyChanging;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);
        StateChanged += (_, _) => PreviewHost.SetWindowMinimized(WindowState == WindowState.Minimized);
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private unsafe void OnSourceInitialized(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
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

    private static bool TryFocus(IInputElement? candidate) => candidate switch
    {
        UIElement element when element.IsEnabled && element.IsVisible && element.Focusable => element.Focus(),
        ContentElement element when element.IsEnabled && element.Focusable => element.Focus(),
        _ => false
    };

    private void ApplyResponsiveLayout(double windowWidth)
    {
        var stacked = ResponsiveLayoutPolicy.UsesStackedSelectors(windowWidth);
        Grid.SetColumn(FormatPanel, stacked ? 0 : 1);
        Grid.SetRow(FormatPanel, stacked ? 1 : 0);
        FormatPanel.Margin = stacked ? new Thickness(0, 14, 0, 0) : new Thickness(14, 0, 0, 0);

        Grid.SetColumn(ActionButtons, stacked ? 0 : 1);
        Grid.SetRow(ActionButtons, stacked ? 1 : 0);
        ActionButtons.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        ActionButtons.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;
        _ = e;
        Dispose();
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
        viewModel.PropertyChanging -= OnViewModelPropertyChanging;
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Dispose();
        GC.SuppressFinalize(this);
    }
}
