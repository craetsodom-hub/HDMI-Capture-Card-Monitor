using System.Windows;
using HdmiCaptureCardMonitor.Infrastructure;
using HdmiCaptureCardMonitor.ViewModels;

namespace HdmiCaptureCardMonitor;

public partial class MainWindow : Window
{
    public MainWindow(IApplicationLogger logger)
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(logger);
    }
}
