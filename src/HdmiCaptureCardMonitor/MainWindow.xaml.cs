using System.Windows;
using HdmiCaptureCardMonitor.Capture.Abstractions;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor;

public partial class MainWindow : Window
{
    public MainWindow(IApplicationLogger logger, ICaptureDeviceDiscoveryService discoveryService, string? startupNotice = null)
    {
        InitializeComponent();
        var viewModel = new MainWindowViewModel(logger, startupNotice, discoveryService: discoveryService);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.StartInitialDiscovery();
        Closed += (_, _) => viewModel.Dispose();
    }
}
