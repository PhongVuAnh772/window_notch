using System;

namespace WindowsNotch.Services.Hotkeys;

/// <summary>
/// Event-driven native Win32 global hotkey service contract.
/// Responsible only for <c>RegisterHotKey</c>, <c>UnregisterHotKey</c>, and <c>WM_HOTKEY</c> routing.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the global screenshot hotkey (<c>Ctrl+Shift+S</c>) is currently registered with Windows.
    /// </summary>
    bool IsRegistered { get; }

    /// <summary>
    /// Gets a value indicating whether registration was attempted and failed (for example, if another application already owns the shortcut).
    /// </summary>
    bool HasRegistrationFailed { get; }

    /// <summary>
    /// Raised when the registered screenshot global hotkey (<c>Ctrl+Shift+S</c>) is pressed.
    /// </summary>
    event EventHandler? ScreenshotHotkeyPressed;

    /// <summary>
    /// Registers the global screenshot hotkey once on the specified <paramref name="windowHandle"/>.
    /// Safe to call if registration fails; never throws or retries in a loop.
    /// </summary>
    bool Start(nint windowHandle);

    /// <summary>
    /// Routes a native <c>WM_HOTKEY</c> message identifier from the owning window procedure.
    /// </summary>
    void OnHotkeyMessageReceived(int hotkeyId);

    /// <summary>
    /// Unregisters the global screenshot hotkey if currently registered.
    /// </summary>
    void Stop();
}
