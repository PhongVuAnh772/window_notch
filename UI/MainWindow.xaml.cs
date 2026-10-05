using Microsoft.UI.Xaml;
using WindowsNotch.AppConfiguration;
using WindowsNotch.Platform;

namespace WindowsNotch.UI;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = AppMetadata.ApplicationName;
        StatusText.Text = WindowsPlatformInfo.GetEnvironmentSummary();
    }
}
