using System;

namespace WindowsNotch.Services.Tray;

/// <summary>
/// UI-independent contract for the native Windows System Tray icon and background utility commands.
/// </summary>
public interface ISystemTrayService : IDisposable
{
    /// <summary>
    /// Gets whether the system tray icon is currently registered and active.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets whether the initial system tray registration attempt failed.
    /// </summary>
    bool HasRegistrationFailed { get; }

    /// <summary>
    /// Raised when the user selects "Show Notch" from the system tray menu.
    /// </summary>
    event EventHandler? ShowRequested;

    /// <summary>
    /// Raised when the user selects "Hide Notch" from the system tray menu.
    /// </summary>
    event EventHandler? HideRequested;

    /// <summary>
    /// Raised when the user selects "Exit" from the system tray menu.
    /// </summary>
    event EventHandler? ExitRequested;

    /// <summary>
    /// Registers the native system tray icon once for the specified window handle.
    /// </summary>
    bool Start(nint hwnd);

    /// <summary>
    /// Synchronizes the current Notch visibility state with the tray service.
    /// </summary>
    void UpdateNotchVisibility(bool isNotchVisible);

    /// <summary>
    /// Processes a native <c>WM_APP + 1</c> tray callback message routed from the window procedure.
    /// </summary>
    void OnTrayMessageReceived(nuint wParam, nint lParam, bool isNotchVisible);

    /// <summary>
    /// Removes the native system tray icon if registered. Safe to call multiple times.
    /// </summary>
    void Stop();
}
