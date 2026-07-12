using System.Windows;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor;

public partial class MainWindow : Window
{
    public MainWindow(IApplicationLogger logger, string? startupNotice = null)
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(logger, startupNotice);
    }
}
