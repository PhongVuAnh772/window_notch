using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsNotch.Services.Tray;

/// <summary>
/// Native Win32 System Tray service using <c>Shell_NotifyIconW</c>, <c>NOTIFYICONDATAW</c>, and <c>TrackPopupMenuEx</c>
/// on the existing <c>NotchWindow</c> HWND without third-party packages, XAML popups, or polling.
/// </summary>
public sealed class WindowsSystemTrayService : ISystemTrayService
{
    public const uint WM_APP = 0x8000;
    public const uint TrayCallbackMessage = WM_APP + 1;
    public const string TrayTooltipText = "WindowsNotch";

    private const uint TrayIconId = 0x2001;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint WM_NULL = 0x0000;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_ENABLED = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_DISABLED = 0x00000002;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;

    private const int CommandHeader = 1000;
    private const int CommandShowNotch = 1001;
    private const int CommandHideNotch = 1002;
    private const int CommandExit = 1003;

    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;
    private static readonly nint IDI_APPLICATION = 32512;

    private readonly object _stateLock = new();
    private nint _registeredHwnd;
    private nint _iconHandle;
    private bool _ownsIconHandle;
    private bool _hasAttemptedRegistration;
    private bool _isRegistered;
    private bool _hasRegistrationFailed;
    private bool _isNotchVisible = true;
    private bool _isMenuOpen;
    private bool _hasExitBeenRequested;
    private bool _isDisposed;

    public bool IsAvailable
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

    public event EventHandler? ShowRequested;

    public event EventHandler? HideRequested;

    public event EventHandler? ExitRequested;

    public bool Start(nint hwnd)
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

            if (hwnd == nint.Zero)
            {
                _hasRegistrationFailed = true;
                Debug.WriteLine("System tray unavailable");
                return false;
            }

            try
            {
                _iconHandle = CreateTrayIcon(out _ownsIconHandle);

                NOTIFYICONDATAW data = CreateNotifyIconData(hwnd, _iconHandle);
                bool added = Shell_NotifyIconW(NIM_ADD, ref data);
                if (added)
                {
                    _registeredHwnd = hwnd;
                    _isRegistered = true;
                    _hasRegistrationFailed = false;
                    return true;
                }
            }
            catch
            {
                // Fall through to safe failure handling below.
            }

            ReleaseOwnedIcon();
            _registeredHwnd = nint.Zero;
            _isRegistered = false;
            _hasRegistrationFailed = true;
            Debug.WriteLine("System tray unavailable");
            return false;
        }
    }

    public void UpdateNotchVisibility(bool isNotchVisible)
    {
        lock (_stateLock)
        {
            if (_isDisposed || !_isRegistered || _registeredHwnd == nint.Zero)
            {
                _isNotchVisible = isNotchVisible;
                return;
            }

            if (_isNotchVisible == isNotchVisible)
            {
                return;
            }

            _isNotchVisible = isNotchVisible;

            try
            {
                NOTIFYICONDATAW data = CreateNotifyIconData(_registeredHwnd, _iconHandle);
                _ = Shell_NotifyIconW(NIM_MODIFY, ref data);
            }
            catch
            {
                // Ignore transient shell notification modification errors.
            }
        }
    }

    public void OnTrayMessageReceived(nuint wParam, nint lParam, bool isNotchVisible)
    {
        nint hwnd;

        lock (_stateLock)
        {
            if (_isDisposed || !_isRegistered || _registeredHwnd == nint.Zero)
            {
                return;
            }

            _isNotchVisible = isNotchVisible;

            uint iconId = unchecked((uint)(wParam & 0xFFFF));
            if (iconId != 0 && iconId != TrayIconId)
            {
                return;
            }

            uint mouseMessage = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
            if (mouseMessage is not (WM_RBUTTONUP or WM_CONTEXTMENU))
            {
                return;
            }

            if (_isMenuOpen)
            {
                return;
            }

            _isMenuOpen = true;
            hwnd = _registeredHwnd;
        }

        try
        {
            ShowContextMenuAndDispatchCommand(hwnd, isNotchVisible);
        }
        catch
        {
            // Never allow native menu or subscriber exceptions to escape into WndProc.
        }
        finally
        {
            lock (_stateLock)
            {
                _isMenuOpen = false;
            }
        }
    }

    public void Stop()
    {
        nint hwndToDelete;
        nint iconToDelete;

        lock (_stateLock)
        {
            if (!_isRegistered)
            {
                ReleaseOwnedIcon();
                return;
            }

            hwndToDelete = _registeredHwnd;
            iconToDelete = _iconHandle;
            _registeredHwnd = nint.Zero;
            _isRegistered = false;
        }

        if (hwndToDelete != nint.Zero)
        {
            try
            {
                NOTIFYICONDATAW data = CreateNotifyIconData(hwndToDelete, iconToDelete);
                _ = Shell_NotifyIconW(NIM_DELETE, ref data);
            }
            catch
            {
                // Safe no-op if shell or HWND was already torn down.
            }
        }

        lock (_stateLock)
        {
            ReleaseOwnedIcon();
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
            ShowRequested = null;
            HideRequested = null;
            ExitRequested = null;
        }
    }

    private void ShowContextMenuAndDispatchCommand(nint hwnd, bool isNotchVisible)
    {
        if (!GetCursorPos(out POINT cursorPoint))
        {
            return;
        }

        nint hMenu = CreatePopupMenu();
        if (hMenu == nint.Zero)
        {
            return;
        }

        int selectedCommand = 0;
        try
        {
            uint showFlags = MF_STRING | (isNotchVisible ? (MF_GRAYED | MF_DISABLED) : MF_ENABLED);
            uint hideFlags = MF_STRING | (!isNotchVisible ? (MF_GRAYED | MF_DISABLED) : MF_ENABLED);

            _ = AppendMenuW(hMenu, MF_STRING | MF_GRAYED | MF_DISABLED, CommandHeader, TrayTooltipText);
            _ = AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            _ = AppendMenuW(hMenu, showFlags, CommandShowNotch, "Show Notch");
            _ = AppendMenuW(hMenu, hideFlags, CommandHideNotch, "Hide Notch");
            _ = AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            _ = AppendMenuW(hMenu, MF_STRING | MF_ENABLED, CommandExit, "Exit");

            nint previousForegroundHwnd = GetForegroundWindow();

            // Standard Windows tray-menu requirement so clicking outside dismisses the popup menu cleanly.
            _ = SetForegroundWindow(hwnd);

            selectedCommand = TrackPopupMenuEx(
                hMenu,
                TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD,
                cursorPoint.X,
                cursorPoint.Y,
                hwnd,
                nint.Zero);

            _ = PostMessageW(hwnd, WM_NULL, 0, 0);

            if (previousForegroundHwnd != nint.Zero && previousForegroundHwnd != hwnd)
            {
                _ = SetForegroundWindow(previousForegroundHwnd);
            }
        }
        finally
        {
            _ = DestroyMenu(hMenu);
        }

        switch (selectedCommand)
        {
            case CommandShowNotch:
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;

            case CommandHideNotch:
                HideRequested?.Invoke(this, EventArgs.Empty);
                break;

            case CommandExit:
                bool shouldRaiseExit = false;
                lock (_stateLock)
                {
                    if (!_isDisposed && !_hasExitBeenRequested)
                    {
                        _hasExitBeenRequested = true;
                        shouldRaiseExit = true;
                    }
                }

                if (shouldRaiseExit)
                {
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                }
                break;
        }
    }

    private static NOTIFYICONDATAW CreateNotifyIconData(nint hwnd, nint hIcon)
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = hIcon,
            szTip = TrayTooltipText,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static nint CreateTrayIcon(out bool ownsIconHandle)
    {
        ownsIconHandle = false;

        try
        {
            int width = Math.Clamp(GetSystemMetrics(SM_CXSMICON), 16, 32);
            int height = Math.Clamp(GetSystemMetrics(SM_CYSMICON), 16, 32);

            BITMAPINFO bmi = new();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            nint hColorBitmap = CreateDIBSection(nint.Zero, ref bmi, DIB_RGB_COLORS, out nint bitsPtr, nint.Zero, 0);
            if (hColorBitmap != nint.Zero && bitsPtr != nint.Zero)
            {
                nint hMaskBitmap = CreateBitmap(width, height, 1, 1, nint.Zero);
                try
                {
                    if (hMaskBitmap != nint.Zero)
                    {
                        RenderNotchPillTrayIconPixels(bitsPtr, width, height);

                        ICONINFO iconInfo = new()
                        {
                            fIcon = true,
                            xHotspot = 0,
                            yHotspot = 0,
                            hbmMask = hMaskBitmap,
                            hbmColor = hColorBitmap
                        };

                        nint createdIcon = CreateIconIndirect(ref iconInfo);
                        if (createdIcon != nint.Zero)
                        {
                            ownsIconHandle = true;
                            return createdIcon;
                        }
                    }
                }
                finally
                {
                    if (hMaskBitmap != nint.Zero)
                    {
                        _ = DeleteObject(hMaskBitmap);
                    }

                    _ = DeleteObject(hColorBitmap);
                }
            }
        }
        catch
        {
            // Fall back to system application icon below.
        }

        return LoadIconW(nint.Zero, IDI_APPLICATION);
    }

    private static void RenderNotchPillTrayIconPixels(nint bitsPtr, int width, int height)
    {
        int[] pixels = new int[width * height];

        float pillLeft = width * 0.08f;
        float pillRight = width * 0.92f;
        float pillTop = height * 0.24f;
        float pillBottom = height * 0.76f;
        float pillHeight = pillBottom - pillTop;
        float radius = pillHeight * 0.5f;

        float leftCenterX = pillLeft + radius;
        float rightCenterX = pillRight - radius;
        float centerY = (pillTop + pillBottom) * 0.5f;

        const uint fillColor = 0xFF14141A;
        const uint borderColor = 0xFFF2F4F8;
        const uint dotColor = 0xFFFFFFFF;

        float dotRadius = Math.Max(1.2f, height * 0.095f);
        float dotCenterX = width * 0.5f;

        for (int y = 0; y < height; y++)
        {
            float py = y + 0.5f;
            for (int x = 0; x < width; x++)
            {
                float px = x + 0.5f;

                float clampedX = Math.Clamp(px, leftCenterX, rightCenterX);
                float dx = px - clampedX;
                float dy = py - centerY;
                float distFromCenterLine = MathF.Sqrt((dx * dx) + (dy * dy));

                if (distFromCenterLine <= radius)
                {
                    float dotDist = MathF.Sqrt(((px - dotCenterX) * (px - dotCenterX)) + (dy * dy));
                    if (dotDist <= dotRadius)
                    {
                        pixels[(y * width) + x] = unchecked((int)dotColor);
                    }
                    else if (distFromCenterLine >= radius - 1.25f)
                    {
                        pixels[(y * width) + x] = unchecked((int)borderColor);
                    }
                    else
                    {
                        pixels[(y * width) + x] = unchecked((int)fillColor);
                    }
                }
            }
        }

        Marshal.Copy(pixels, 0, bitsPtr, pixels.Length);
    }

    private void ReleaseOwnedIcon()
    {
        if (_ownsIconHandle && _iconHandle != nint.Zero)
        {
            try
            {
                _ = DestroyIcon(_iconHandle);
            }
            catch
            {
                // Ignore cleanup failure during teardown.
            }
        }

        _iconHandle = nint.Zero;
        _ownsIconHandle = false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int TrackPopupMenuEx(nint hMenu, uint fuFlags, int x, int y, nint hwnd, nint lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hWnd, uint Msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
    private static extern nint LoadIconW(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint hdc,
        ref BITMAPINFO pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateBitmap(
        int nWidth,
        int nHeight,
        uint nPlanes,
        uint nBitCount,
        nint lpBits);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
