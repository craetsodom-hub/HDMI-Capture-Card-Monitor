using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.Models;
using HdmiCaptureCardMonitor.Presentation;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor;

public partial class MainWindow : Window, IDisposable
{
    private readonly MainWindowViewModel viewModel;
    private bool isDisposed;

    public MainWindow(IApplicationLogger logger, ICaptureDeviceDiscoveryService discoveryService, ICapturePreviewService previewService, string? startupNotice = null)
    {
        InitializeComponent();
        viewModel = new MainWindowViewModel(logger, startupNotice, discoveryService: discoveryService, previewService: previewService, previewSurface: PreviewHost);
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        SizeChanged += (_, _) => ApplyResponsiveLayout(ActualWidth);
        StateChanged += (_, _) => PreviewHost.SetWindowMinimized(WindowState == WindowState.Minimized);
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ApplyResponsiveLayout(ActualWidth);
        viewModel.StartInitialDiscovery();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (e.PropertyName != nameof(MainWindowViewModel.IsInformationDialogOpen)) return;

        var dialogOpen = viewModel.IsInformationDialogOpen;
        if (dialogOpen)
        {
            PreviewHost.SetVideoVisible(false);
            PreviewHost.SetSurfaceActive(false);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, DialogPrimaryButton.Focus);
            return;
        }

        PreviewHost.SetSurfaceActive(viewModel.IsPreviewActive);
        PreviewHost.SetVideoVisible(viewModel.SessionState == CaptureSessionState.Previewing);
    }

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
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        viewModel.Dispose();
        GC.SuppressFinalize(this);
    }
}
