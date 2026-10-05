using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsNotch.Services.Hotkeys;

/// <summary>
/// Native Win32 global hotkey service using <c>RegisterHotKey</c> and <c>UnregisterHotKey</c> on the existing NotchWindow HWND.
/// Registers <c>Ctrl+Shift+S</c> once without keyboard hooks, background threads, or polling.
/// </summary>
public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    public const string ScreenshotShortcutDescription = "Ctrl+Shift+S";

    private const int ScreenshotHotkeyId = 0x1001;

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_S = 0x53;

    private readonly object _stateLock = new();
    private nint _registeredWindowHandle;
    private bool _hasAttemptedRegistration;
    private bool _isRegistered;
    private bool _hasRegistrationFailed;
    private bool _isDisposed;

    public bool IsRegistered
    {
        get
        {
            lock (_stateLock)
            {
                return _isRegistered && !_isDisposed;
            }
        }
    }

    public bool HasRegistrationFailed
    {
        get
        {
            lock (_stateLock)
            {
                return _hasRegistrationFailed;
            }
        }
    }

    public event EventHandler? ScreenshotHotkeyPressed;

    public bool Start(nint windowHandle)
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            if (_isRegistered)
            {
                return true;
            }

            // Never retry registration repeatedly after an initial attempt.
            if (_hasAttemptedRegistration)
            {
                return false;
            }

            _hasAttemptedRegistration = true;

            if (windowHandle == nint.Zero)
            {
                _hasRegistrationFailed = true;
                Debug.WriteLine("Screenshot hotkey unavailable");
                return false;
            }

            try
            {
                bool registered = RegisterHotKey(
                    windowHandle,
                    ScreenshotHotkeyId,
                    MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT,
                    VK_S);

                if (!registered)
                {
                    registered = RegisterHotKey(
                        windowHandle,
                        ScreenshotHotkeyId,
                        MOD_CONTROL | MOD_SHIFT,
                        VK_S);
                }

                if (registered)
                {
                    _registeredWindowHandle = windowHandle;
                    _isRegistered = true;
                    _hasRegistrationFailed = false;
                    return true;
                }
            }
            catch
            {
                // Fall through to safe failure state below.
            }

            _registeredWindowHandle = nint.Zero;
            _isRegistered = false;
            _hasRegistrationFailed = true;
            Debug.WriteLine("Screenshot hotkey unavailable");
            return false;
        }
    }

    public void OnHotkeyMessageReceived(int hotkeyId)
    {
        if (hotkeyId != ScreenshotHotkeyId)
        {
            return;
        }

        lock (_stateLock)
        {
            if (_isDisposed || !_isRegistered)
            {
                return;
            }
        }

        try
        {
            ScreenshotHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Never allow subscriber exceptions to escape into the native WndProc.
        }
    }

    public void Stop()
    {
        nint hwndToUnregister;

        lock (_stateLock)
        {
            if (!_isRegistered)
            {
                return;
            }

            hwndToUnregister = _registeredWindowHandle;
            _registeredWindowHandle = nint.Zero;
            _isRegistered = false;
        }

        if (hwndToUnregister != nint.Zero)
        {
            try
            {
                _ = UnregisterHotKey(hwndToUnregister, ScreenshotHotkeyId);
            }
            catch
            {
                // Safe no-op if the HWND was already destroyed by the OS before Stop().
            }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
            {
                return;
            }
        }

        Stop();

        lock (_stateLock)
        {
            _isDisposed = true;
            ScreenshotHotkeyPressed = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
