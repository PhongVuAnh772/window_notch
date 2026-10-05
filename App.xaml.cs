using System;
using Microsoft.UI.Xaml;
using WindowsNotch.UI;

namespace WindowsNotch;

public partial class App : Application
{
    private NotchWindow? _notchWindow;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();
    }

    public bool IsNotchVisible => _notchWindow is { IsNotchVisible: true };

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (_notchWindow is not null)
        {
            _notchWindow.ShowNotch();
            return;
        }

        _notchWindow = new NotchWindow();
        _notchWindow.ApplicationExitRequested += OnNotchWindowApplicationExitRequested;
        _notchWindow.Activate();
    }

    public void ShowNotch()
    {
        _notchWindow?.ShowNotch();
    }

    public void HideNotch()
    {
        _notchWindow?.HideNotch();
    }

    public void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        if (_notchWindow is not null)
        {
            _notchWindow.ApplicationExitRequested -= OnNotchWindowApplicationExitRequested;
            _notchWindow.ShutdownAndClose();
            _notchWindow = null;
        }

        Exit();
    }

    private void OnNotchWindowApplicationExitRequested(object? sender, EventArgs e)
    {
        ExitApplication();
    }
}
