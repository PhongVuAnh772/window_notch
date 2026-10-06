using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace WindowsNotch.Platform.Windows;

/// <summary>
/// Target monitor selection strategy.
/// Step 2 uses <see cref="Primary"/>; additional modes are prepared for future multi-monitor milestones.
/// </summary>
public enum MonitorPlacementTarget
{
    Primary = 0,
    ActiveWindowMonitor,
    CursorMonitor,
    UserSelectedMonitor
}

public readonly record struct MonitorWorkArea(
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi);

public readonly record struct NativeTrayMessageEventArgs(
    nuint WParam,
    nint LParam);

/// <summary>
/// Manages native WinUI 3 AppWindow and Win32/DWM configuration for the persistent Notch window.
/// Handles borderless styling, per-pixel outer transparency, always-on-top z-order, taskbar suppression,
/// NonClient Passthrough hit-testing, and DPI-aware top-center positioning.
/// </summary>
public sealed class WindowManager : IDisposable
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private static readonly nint WS_POPUP = unchecked((int)0x80000000);
    private const nint WS_CAPTION = 0x00C00000;
    private const nint WS_BORDER = 0x00800000;
    private const nint WS_DLGFRAME = 0x00400000;
    private const nint WS_THICKFRAME = 0x00040000;
    private const nint WS_MINIMIZEBOX = 0x00020000;
    private const nint WS_MAXIMIZEBOX = 0x00010000;
    private const nint WS_SYSMENU = 0x00080000;

    private const nint WS_EX_TOPMOST = 0x00000008;
    private const nint WS_EX_TOOLWINDOW = 0x00000080;
    private const nint WS_EX_APPWINDOW = 0x00040000;
    private const nint WS_EX_NOACTIVATE = 0x08000000;

    private static readonly nint HWND_TOPMOST = -1;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const int SW_HIDE = 0;

    private const uint WM_PAINT = 0x000F;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const uint WM_APP = 0x8000;
    private const uint TrayCallbackMessage = WM_APP + 1;
    private const nuint SPI_SETWORKAREA = 0x002F;
    private const nint HTTRANSPARENT = -1;
    private const int BLACK_BRUSH = 4;

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint StandardDpi = 96;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint DWM_BB_ENABLE = 0x00000001;
    private const uint DWM_BB_BLURREGION = 0x00000002;

    private const nuint SubclassId = 1001;

    private readonly Window _window;
    private readonly nint _hwnd;
    private readonly WindowId _windowId;
    private readonly AppWindow _appWindow;
    private readonly SubclassProc _subclassDelegate;
    private bool _isSubclassed;
    private bool _allowWindowClose;
    private bool _isDisposed;

    private double _currentLogicalWidth;
    private double _currentLogicalHeight;
    private double _interactiveLogicalWidth;
    private double _interactiveLogicalHeight;
    private int _appliedLeft = int.MinValue;
    private int _appliedTop = int.MinValue;
    private int _appliedPhysicalWidth = -1;
    private int _appliedPhysicalHeight = -1;
    private int _interactivePhysicalLeft;
    private int _interactivePhysicalTop;
    private int _interactivePhysicalRight;
    private int _interactivePhysicalBottom;
    private bool _wasCursorInsideInteractivePill;
    private MonitorWorkArea _cachedWorkArea;
    private bool _hasCachedWorkArea;
    private MonitorPlacementTarget _monitorTarget = MonitorPlacementTarget.Primary;
    private global::Windows.UI.Composition.CompositionColorBrush? _transparentBackdropBrush;

    public WindowManager(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _hwnd = WindowNative.GetWindowHandle(_window);
        _windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(_windowId);
        _subclassDelegate = WindowSubclassProc;
    }

    public nint WindowHandle => _hwnd;

    public bool IsWindowVisible { get; private set; } = true;

    public event EventHandler? ClipboardMessageReceived;

    public event EventHandler<int>? NativeHotkeyPressed;

    public event EventHandler<NativeTrayMessageEventArgs>? NativeTrayMessageReceived;

    public event EventHandler? WindowCloseRequested;

    public event EventHandler? CursorOutsideInteractiveBoundsDetected;

    public void InitializeNotchShell(
        double hostLogicalWidth,
        double hostLogicalHeight,
        double interactiveLogicalWidth,
        double interactiveLogicalHeight)
    {
        _currentLogicalWidth = hostLogicalWidth;
        _currentLogicalHeight = hostLogicalHeight;
        _interactiveLogicalWidth = interactiveLogicalWidth;
        _interactiveLogicalHeight = interactiveLogicalHeight;
        IsWindowVisible = true;

        ConfigureAppWindowPresenter();
        ConfigureWin32WindowStyles();
        ConfigurePerPixelTransparency();
        InstallWindowSubclass();
        RefreshMonitorWorkArea(_monitorTarget);
        UpdatePositionAndSize(_currentLogicalWidth, _currentLogicalHeight, _monitorTarget, forceUpdate: true);
    }

    /// <summary>
    /// Re-applies borderless Win32 styles, DWM per-pixel transparency, and NonClient passthrough regions
    /// after <see cref="Window.Activate"/> so WinUI 3's initial activation cannot restore non-client chrome.
    /// </summary>
    public void EnsureStylesOnActivation()
    {
        if (_isDisposed)
        {
            return;
        }

        ConfigureWin32WindowStyles();
        ConfigurePerPixelTransparency();
        RefreshMonitorWorkArea(_monitorTarget);
        UpdatePositionAndSize(_currentLogicalWidth, _currentLogicalHeight, _monitorTarget, forceUpdate: true);
    }

    public void AllowWindowClose()
    {
        _allowWindowClose = true;
    }

    public void ShowNotchWindow(
        double logicalWidth,
        double logicalHeight,
        MonitorPlacementTarget target = MonitorPlacementTarget.Primary)
    {
        if (_isDisposed)
        {
            return;
        }

        IsWindowVisible = true;
        RefreshMonitorWorkArea(target);
        UpdatePositionAndSize(logicalWidth, logicalHeight, target, forceUpdate: true);
    }

    public void HideNotchWindow()
    {
        if (_isDisposed)
        {
            return;
        }

        IsWindowVisible = false;
        _wasCursorInsideInteractivePill = false;
        _ = SetWindowPos(
            _hwnd,
            nint.Zero,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        _ = ShowWindow(_hwnd, SW_HIDE);
    }

    public void SetInteractivePillBounds(double interactiveLogicalWidth, double interactiveLogicalHeight)
    {
        if (_isDisposed)
        {
            return;
        }

        _interactiveLogicalWidth = interactiveLogicalWidth;
        _interactiveLogicalHeight = interactiveLogicalHeight;
        UpdateInteractiveBoundsAndPassthroughRegions();
    }

    public void UpdatePositionAndSize(
        double logicalWidth,
        double logicalHeight,
        MonitorPlacementTarget target = MonitorPlacementTarget.Primary,
        bool forceUpdate = false)
    {
        _currentLogicalWidth = logicalWidth;
        _currentLogicalHeight = logicalHeight;
        _monitorTarget = target;

        if (!_hasCachedWorkArea || forceUpdate)
        {
            RefreshMonitorWorkArea(target);
        }

        MonitorWorkArea workArea = _cachedWorkArea;
        double scaleFactor = workArea.Dpi / (double)StandardDpi;

        int physicalWidth = (int)Math.Round(logicalWidth * scaleFactor);
        int physicalHeight = (int)Math.Round(logicalHeight * scaleFactor);

        int screenCenterX = workArea.Left + (workArea.Width / 2);
        int notchLeft = screenCenterX - (physicalWidth / 2);
        int notchTop = workArea.Top;

        if (!forceUpdate &&
            notchLeft == _appliedLeft &&
            notchTop == _appliedTop &&
            physicalWidth == _appliedPhysicalWidth &&
            physicalHeight == _appliedPhysicalHeight)
        {
            UpdateInteractiveBoundsAndPassthroughRegions();
            return;
        }

        _appliedLeft = notchLeft;
        _appliedTop = notchTop;
        _appliedPhysicalWidth = physicalWidth;
        _appliedPhysicalHeight = physicalHeight;

        UpdateInteractiveBoundsAndPassthroughRegions();

        uint visibilityFlag = IsWindowVisible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW;

        SetWindowPos(
            _hwnd,
            HWND_TOPMOST,
            notchLeft,
            notchTop,
            physicalWidth,
            physicalHeight,
            SWP_NOACTIVATE | visibilityFlag);
    }

    private void UpdateInteractiveBoundsAndPassthroughRegions()
    {
        if (!_hasCachedWorkArea || _appliedPhysicalWidth <= 0 || _appliedPhysicalHeight <= 0)
        {
            return;
        }

        double scaleFactor = _cachedWorkArea.Dpi / (double)StandardDpi;
        int pillPhysicalWidth = Math.Clamp((int)Math.Round(_interactiveLogicalWidth * scaleFactor), 0, _appliedPhysicalWidth);
        int pillPhysicalHeight = Math.Clamp((int)Math.Round(_interactiveLogicalHeight * scaleFactor), 0, _appliedPhysicalHeight);

        int localLeft = Math.Max(0, (_appliedPhysicalWidth - pillPhysicalWidth) / 2);
        int localTop = 0;
        int localRight = Math.Min(_appliedPhysicalWidth, localLeft + pillPhysicalWidth);
        int localBottom = Math.Min(_appliedPhysicalHeight, pillPhysicalHeight);

        _interactivePhysicalLeft = _appliedLeft + localLeft;
        _interactivePhysicalTop = _appliedTop + localTop;
        _interactivePhysicalRight = _appliedLeft + localRight;
        _interactivePhysicalBottom = _appliedTop + localBottom;

        ApplyNonClientPassthroughRegions(localLeft, localRight, localBottom, _appliedPhysicalWidth, _appliedPhysicalHeight);
    }

    private void ApplyNonClientPassthroughRegions(
        int pillLocalLeft,
        int pillLocalRight,
        int pillLocalBottom,
        int hostPhysicalWidth,
        int hostPhysicalHeight)
    {
        if (_isDisposed || hostPhysicalWidth <= 0 || hostPhysicalHeight <= 0)
        {
            return;
        }

        try
        {
            InputNonClientPointerSource? nonClientSource = InputNonClientPointerSource.GetForWindowId(_windowId);
            if (nonClientSource is null)
            {
                return;
            }

            List<RectInt32> passthroughRects = new(3);

            if (pillLocalLeft > 0)
            {
                passthroughRects.Add(new RectInt32(0, 0, pillLocalLeft, hostPhysicalHeight));
            }

            if (hostPhysicalWidth > pillLocalRight)
            {
                passthroughRects.Add(new RectInt32(pillLocalRight, 0, hostPhysicalWidth - pillLocalRight, hostPhysicalHeight));
            }

            int centerWidth = pillLocalRight - pillLocalLeft;
            if (centerWidth > 0 && hostPhysicalHeight > pillLocalBottom)
            {
                passthroughRects.Add(new RectInt32(pillLocalLeft, pillLocalBottom, centerWidth, hostPhysicalHeight - pillLocalBottom));
            }

            if (passthroughRects.Count > 0)
            {
                nonClientSource.SetRegionRects(NonClientRegionKind.Passthrough, passthroughRects.ToArray());
            }
            else
            {
                nonClientSource.ClearRegionRects(NonClientRegionKind.Passthrough);
            }
        }
        catch
        {
            // Ignore if InputNonClientPointerSource is not yet attached or during window teardown.
        }
    }

    public bool IsCursorInsideLogicalBounds(double logicalWidth, double logicalHeight)
    {
        if (!GetCursorPos(out POINT cursorPoint))
        {
            return false;
        }

        return IsScreenPointInsideLogicalBounds(cursorPoint.X, cursorPoint.Y, logicalWidth, logicalHeight);
    }

    public bool IsScreenPointInsideInteractivePill(int screenX, int screenY)
    {
        if (_interactiveLogicalWidth <= 0 || _interactiveLogicalHeight <= 0)
        {
            return false;
        }

        if (_interactivePhysicalRight > _interactivePhysicalLeft &&
            _interactivePhysicalBottom > _interactivePhysicalTop)
        {
            return screenX >= _interactivePhysicalLeft &&
                   screenX < _interactivePhysicalRight &&
                   screenY >= _interactivePhysicalTop &&
                   screenY < _interactivePhysicalBottom;
        }

        return IsScreenPointInsideLogicalBounds(screenX, screenY, _interactiveLogicalWidth, _interactiveLogicalHeight);
    }

    public bool IsScreenPointInsideLogicalBounds(
        int screenX,
        int screenY,
        double logicalWidth,
        double logicalHeight)
    {
        if (!_hasCachedWorkArea)
        {
            RefreshMonitorWorkArea(_monitorTarget);
        }

        MonitorWorkArea workArea = _cachedWorkArea;
        double scaleFactor = workArea.Dpi / (double)StandardDpi;

        int physicalWidth = (int)Math.Round(logicalWidth * scaleFactor);
        int physicalHeight = (int)Math.Round(logicalHeight * scaleFactor);

        int screenCenterX = workArea.Left + (workArea.Width / 2);
        int left = screenCenterX - (physicalWidth / 2);
        int top = workArea.Top;
        int right = left + physicalWidth;
        int bottom = top + physicalHeight;

        return screenX >= left &&
               screenX < right &&
               screenY >= top &&
               screenY < bottom;
    }

    private void ConfigureAppWindowPresenter()
    {
        // Hide the Notch from the Windows taskbar and Alt+Tab switcher.
        _appWindow.IsShownInSwitchers = false;
        _appWindow.Closing -= OnAppWindowClosing;
        _appWindow.Closing += OnAppWindowClosing;

        try
        {
            _window.ExtendsContentIntoTitleBar = true;
        }
        catch
        {
            // Ignore if custom title bar extension is not supported in the current presenter state.
        }

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowWindowClose || _isDisposed)
        {
            return;
        }

        args.Cancel = true;

        try
        {
            WindowCloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Never allow subscriber exceptions to escape AppWindow.Closing.
        }
    }

    private void ConfigureWin32WindowStyles()
    {
        // Strip WS_DLGFRAME, WS_BORDER, WS_CAPTION, and WS_THICKFRAME (NonResizableWindowWhiteBorderWorkaround)
        // without forcing WS_POPUP so OverlappedPresenter + ExtendsContentIntoTitleBar remain completely borderless.
        nint style = GetWindowLongPtr(_hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_BORDER | WS_DLGFRAME | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_POPUP);
        SetWindowLongPtr(_hwnd, GWL_STYLE, style);

        // WS_EX_TOOLWINDOW prevents taskbar button creation; WS_EX_NOACTIVATE prevents stealing focus.
        nint exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(
            _hwnd,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void ConfigurePerPixelTransparency()
    {
        // Directly attach a transparent Windows.UI.Composition.CompositionColorBrush to ICompositionSupportsSystemBackdrop
        // so WinUI 3's root visual never renders an opaque white/dark rectangular backdrop behind the Notch pill.
        try
        {
            var brushHolder = _window.As<ICompositionSupportsSystemBackdrop>();
            _transparentBackdropBrush ??= TransparentWindowBackdrop.SystemCompositor.CreateColorBrush(
                global::Windows.UI.Color.FromArgb(0, 255, 255, 255));
            brushHolder.SystemBackdrop = _transparentBackdropBrush;
        }
        catch
        {
            // Ignore if ICompositionSupportsSystemBackdrop is temporarily unavailable.
        }

        // Suppress Windows 11 DWM outer rectangular border and system corner rounding on the host HWND.
        if (WindowsPlatformInfo.IsWindows11OrGreater())
        {
            int cornerPreference = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(
                _hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                sizeof(int));

            uint borderColor = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(
                _hwnd,
                DWMWA_BORDER_COLOR,
                ref borderColor,
                sizeof(uint));
        }

        // Reset DWM non-client frame margins to 0 (do NOT pass -1, which draws the white non-client sheet on Win11)
        // and attach an empty (-2,-2,-1,-1) blur region so DirectComposition per-pixel alpha composites over the desktop.
        MARGINS margins = new()
        {
            cxLeftWidth = 0,
            cxRightWidth = 0,
            cyTopHeight = 0,
            cyBottomHeight = 0
        };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        nint emptyRegion = CreateRectRgn(-2, -2, -1, -1);
        try
        {
            DWM_BLURBEHIND blurBehind = new()
            {
                dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
                fEnable = true,
                hRgnBlur = emptyRegion,
                fTransitionOnMaximized = false
            };
            DwmEnableBlurBehindWindow(_hwnd, ref blurBehind);
        }
        finally
        {
            if (emptyRegion != nint.Zero)
            {
                DeleteObject(emptyRegion);
            }
        }

        nint hdc = GetDC(_hwnd);
        if (hdc != nint.Zero)
        {
            try
            {
                ClearClientBackground(hdc);
            }
            finally
            {
                ReleaseDC(_hwnd, hdc);
            }
        }

        _ = InvalidateRect(_hwnd, nint.Zero, true);
    }

    private void ClearClientBackground(nint hdc)
    {
        if (hdc != nint.Zero && GetClientRect(_hwnd, out RECT rect))
        {
            nint blackBrush = GetStockObject(BLACK_BRUSH);
            if (blackBrush != nint.Zero)
            {
                FillRect(hdc, ref rect, blackBrush);
            }
        }
    }

    private void RefreshMonitorWorkArea(MonitorPlacementTarget target)
    {
        _cachedWorkArea = GetMonitorWorkArea(target);
        _hasCachedWorkArea = true;
    }

    private MonitorWorkArea GetMonitorWorkArea(MonitorPlacementTarget target)
    {
        // Step 2 resolves the primary monitor; structure is prepared for future monitor targets.
        nint hMonitor = target switch
        {
            MonitorPlacementTarget.Primary => MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY),
            _ => MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY)
        };

        MONITORINFO monitorInfo = new()
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        uint dpi = GetMonitorDpi(hMonitor);

        if (GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            int left = monitorInfo.rcWork.Left;
            int top = monitorInfo.rcWork.Top;
            int width = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
            int height = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
            return new MonitorWorkArea(left, top, width, height, dpi);
        }

        DisplayArea primaryArea = DisplayArea.Primary;
        return new MonitorWorkArea(
            primaryArea.WorkArea.X,
            primaryArea.WorkArea.Y,
            primaryArea.WorkArea.Width,
            primaryArea.WorkArea.Height,
            dpi);
    }

    private uint GetMonitorDpi(nint hMonitor)
    {
        if (hMonitor != nint.Zero &&
            GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX;
        }

        uint windowDpi = GetDpiForWindow(_hwnd);
        return windowDpi > 0 ? windowDpi : StandardDpi;
    }

    private void InstallWindowSubclass()
    {
        if (!_isSubclassed)
        {
            _isSubclassed = SetWindowSubclass(_hwnd, _subclassDelegate, SubclassId, 0);
        }
    }

    private nint WindowSubclassProc(
        nint hWnd,
        uint uMsg,
        nuint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData)
    {
        switch (uMsg)
        {
            case WM_CLOSE when !_allowWindowClose && !_isDisposed:
                // Closing the Notch via Alt+F4 or WM_CLOSE hides the Notch instead of terminating the background process.
                try
                {
                    WindowCloseRequested?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // Never allow subscriber exceptions to escape the native WndProc.
                }
                return 0;

            case WM_NCCALCSIZE:
                // Returning 0 removes all non-client frame insets so the client area is flush with Y = workAreaTop.
                return 0;

            case WM_NCHITTEST:
                if (!IsWindowVisible)
                {
                    return HTTRANSPARENT;
                }

                // O(1) hit-test using screen coordinates packed in lParam with zero Win32 P/Invokes.
                if (_interactiveLogicalWidth > 0 && _interactiveLogicalHeight > 0)
                {
                    long rawLParam = lParam.ToInt64();
                    int screenX = unchecked((short)(rawLParam & 0xFFFF));
                    int screenY = unchecked((short)((rawLParam >> 16) & 0xFFFF));

                    if (!IsScreenPointInsideInteractivePill(screenX, screenY))
                    {
                        if (_wasCursorInsideInteractivePill)
                        {
                            _wasCursorInsideInteractivePill = false;
                            CursorOutsideInteractiveBoundsDetected?.Invoke(this, EventArgs.Empty);
                        }

                        return HTTRANSPARENT;
                    }

                    _wasCursorInsideInteractivePill = true;
                }
                break;

            case WM_PAINT:
            {
                // Intercept WinUI 3's default WM_PAINT (which fills the HWND redirection bitmap opaque white)
                // and fill with stock BLACK_BRUSH (0x00000000 = premultiplied alpha 0 = 100% transparent).
                nint hdc = BeginPaint(hWnd, out PAINTSTRUCT ps);
                if (hdc != nint.Zero)
                {
                    nint blackBrush = GetStockObject(BLACK_BRUSH);
                    if (blackBrush != nint.Zero)
                    {
                        FillRect(hdc, ref ps.rcPaint, blackBrush);
                    }

                    EndPaint(hWnd, ref ps);
                }

                return 1;
            }

            case WM_ERASEBKGND:
                // Clear GDI background to 0x00000000 so DWM composites the non-pill client area transparently.
                ClearClientBackground((nint)wParam);
                return 1;

            case WM_DWMCOMPOSITIONCHANGED:
                ConfigurePerPixelTransparency();
                return 0;

            case WM_DPICHANGED:
                RefreshMonitorWorkArea(_monitorTarget);
                if (_currentLogicalWidth > 0 && _currentLogicalHeight > 0)
                {
                    UpdatePositionAndSize(_currentLogicalWidth, _currentLogicalHeight, _monitorTarget, forceUpdate: true);
                }
                return 0;

            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE when wParam == SPI_SETWORKAREA:
                RefreshMonitorWorkArea(_monitorTarget);
                if (_currentLogicalWidth > 0 && _currentLogicalHeight > 0)
                {
                    UpdatePositionAndSize(_currentLogicalWidth, _currentLogicalHeight, _monitorTarget, forceUpdate: true);
                }
                break;

            case WM_CLIPBOARDUPDATE:
                ClipboardMessageReceived?.Invoke(this, EventArgs.Empty);
                return 0;

            case WM_HOTKEY:
                if (!_isDisposed)
                {
                    try
                    {
                        NativeHotkeyPressed?.Invoke(this, unchecked((int)wParam));
                    }
                    catch
                    {
                        // Never allow subscriber exceptions to escape the native WndProc.
                    }
                }
                return 0;

            case TrayCallbackMessage:
                if (!_isDisposed)
                {
                    try
                    {
                        NativeTrayMessageReceived?.Invoke(this, new NativeTrayMessageEventArgs(wParam, lParam));
                    }
                    catch
                    {
                        // Never allow subscriber exceptions to escape the native WndProc.
                    }
                }
                return 0;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _appWindow.Closing -= OnAppWindowClosing;

        if (_isSubclassed)
        {
            RemoveWindowSubclass(_hwnd, _subclassDelegate, SubclassId);
            _isSubclassed = false;
        }

        if (_transparentBackdropBrush is not null)
        {
            try
            {
                var brushHolder = _window.As<ICompositionSupportsSystemBackdrop>();
                brushHolder.SystemBackdrop = null;
            }
            catch
            {
                // Ignore during window teardown.
            }

            _transparentBackdropBrush.Dispose();
            _transparentBackdropBrush = null;
        }

        _isDisposed = true;
    }

    private delegate nint SubclassProc(
        nint hWnd,
        uint uMsg,
        nuint wParam,
        nint lParam,
        nuint uIdSubclass,
        nuint dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        private int _rgbReserved0;
        private int _rgbReserved1;
        private int _rgbReserved2;
        private int _rgbReserved3;
        private int _rgbReserved4;
        private int _rgbReserved5;
        private int _rgbReserved6;
        private int _rgbReserved7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fEnable;
        public nint hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fTransitionOnMaximized;
    }

    private static nint GetWindowLongPtr(nint hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : GetWindowLong32(hWnd, nIndex);
    }

    private static nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern nint GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern nint SetWindowLong32(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hWnd,
        nint hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hWnd, ref MARGINS pMarInset);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(nint hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int dwAttribute,
        ref uint pvAttribute,
        int cbAttribute);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int fnObject);

    [DllImport("user32.dll")]
    private static extern nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(nint hWnd, nint lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hDC, ref RECT lprc, nint hbr);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd,
        SubclassProc pfnSubclass,
        nuint uIdSubclass,
        nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd,
        SubclassProc pfnSubclass,
        nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint hWnd,
        uint uMsg,
        nuint wParam,
        nint lParam);
}

/// <summary>
/// Provides a thread-bound <see cref="global::Windows.UI.Composition.Compositor"/> for creating the transparent
/// <see cref="ICompositionSupportsSystemBackdrop"/> brush on the WinUI 3 Notch window.
/// </summary>
public sealed partial class TransparentWindowBackdrop : SystemBackdrop
{
    private static readonly object SyncLock = new();
    private static global::Windows.System.DispatcherQueue? _dispatcherQueue;
    private static global::Windows.System.DispatcherQueueController? _dispatcherQueueController;
    private static global::Windows.UI.Composition.Compositor? _systemCompositor;

    internal static global::Windows.UI.Composition.Compositor SystemCompositor
    {
        get
        {
            if (_systemCompositor is null)
            {
                lock (SyncLock)
                {
                    if (_systemCompositor is null)
                    {
                        _dispatcherQueue = global::Windows.System.DispatcherQueue.GetForCurrentThread()
                            ?? (_dispatcherQueueController = InitializeCoreDispatcher()).DispatcherQueue;
                        _systemCompositor = new global::Windows.UI.Composition.Compositor();
                    }
                }
            }

            return _systemCompositor;
        }
    }

    private static global::Windows.System.DispatcherQueueController InitializeCoreDispatcher()
    {
        DispatcherQueueOptions options = new()
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 2  // DQTAT_COM_STA
        };

        CreateDispatcherQueueController(options, out nint raw);
        return global::Windows.System.DispatcherQueueController.FromAbi(raw);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int dwSize;
        internal int threadType;
        internal int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        [In] DispatcherQueueOptions options,
        out nint dispatcherQueueController);
}
