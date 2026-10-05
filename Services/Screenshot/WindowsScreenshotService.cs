using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowsNotch.Core;

namespace WindowsNotch.Services.Screenshot;

/// <summary>
/// Native Windows primary-display screenshot capture service.
/// Captures the primary monitor on demand into a compact aspect-ratio-preserving in-memory PNG preview (<= 320x180, <= 4 MB)
/// and maintains an immutable in-memory recent history of at most 3 screenshots with atomic concurrent-capture protection,
/// zero polling, zero clipboard modification, zero logging of image data, and zero disk writes.
/// </summary>
public sealed class WindowsScreenshotService : IScreenshotService
{
    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private readonly object _stateGate = new();
    private readonly List<RecentScreenshot> _recentHistory = new(DesignTokens.Screenshot.MaxHistoryCount);
    private IReadOnlyList<RecentScreenshot> _recentSnapshot = Array.Empty<RecentScreenshot>();
    private ScreenshotState _currentState = ScreenshotState.Empty;
    private int _captureInProgress;
    private long _lastCaptureCompletedTick;
    private bool _isDisposed;

    public ScreenshotState CurrentState
    {
        get
        {
            lock (_stateGate)
            {
                return _currentState;
            }
        }
    }

    public IReadOnlyList<RecentScreenshot> RecentScreenshots
    {
        get
        {
            lock (_stateGate)
            {
                return _recentSnapshot;
            }
        }
    }

    public event EventHandler<ScreenshotState>? ScreenshotStateChanged;

    public event EventHandler? RecentScreenshotsChanged;

    public async Task<bool> CapturePrimaryDisplayAsync()
    {
        if (Volatile.Read(ref _isDisposed))
        {
            return false;
        }

        // Atomic guard: allow only one capture operation at a time.
        if (Interlocked.CompareExchange(ref _captureInProgress, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            long nowTick = Environment.TickCount64;
            long lastTick = Interlocked.Read(ref _lastCaptureCompletedTick);
            if (lastTick > 0 && (nowTick - lastTick) < DesignTokens.Screenshot.CaptureCommandCooldownMs)
            {
                return false;
            }

            if (!TryGetPrimaryDisplayBounds(out int left, out int top, out int sourceWidth, out int sourceHeight))
            {
                return false;
            }

            (int previewWidth, int previewHeight) = ScreenshotState.ComputePreviewDimensions(
                sourceWidth,
                sourceHeight,
                DesignTokens.Screenshot.PreviewMaxWidth,
                DesignTokens.Screenshot.PreviewMaxHeight);

            if (previewWidth <= 0 || previewHeight <= 0)
            {
                return false;
            }

            byte[]? rawBgraPixels = TryCaptureDownscaledPrimaryDisplayBgra(
                left,
                top,
                sourceWidth,
                sourceHeight,
                previewWidth,
                previewHeight);

            if (rawBgraPixels is null || rawBgraPixels.Length == 0)
            {
                return false;
            }

            byte[]? encodedPngBytes = await TryEncodeBgraToPngAsync(
                rawBgraPixels,
                previewWidth,
                previewHeight).ConfigureAwait(false);

            // Allow raw pixel array to be reclaimed immediately after encoding.
            rawBgraPixels = null;

            if (encodedPngBytes is null ||
                encodedPngBytes.Length == 0 ||
                encodedPngBytes.Length > DesignTokens.Screenshot.MaxImageBytes)
            {
                return false;
            }

            if (Volatile.Read(ref _isDisposed))
            {
                return false;
            }

            DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;
            RecentScreenshot recentScreenshot = new(
                ImageData: encodedPngBytes,
                Width: sourceWidth,
                Height: sourceHeight,
                CapturedAtUtc: capturedAtUtc);
            ScreenshotState nextState = recentScreenshot.ToScreenshotState();

            lock (_stateGate)
            {
                if (_isDisposed)
                {
                    return false;
                }

                _recentHistory.Insert(0, recentScreenshot);
                while (_recentHistory.Count > DesignTokens.Screenshot.MaxHistoryCount)
                {
                    _recentHistory.RemoveAt(_recentHistory.Count - 1);
                }

                _recentSnapshot = Array.AsReadOnly(_recentHistory.ToArray());
                _currentState = nextState;
            }

            Interlocked.Exchange(ref _lastCaptureCompletedTick, Environment.TickCount64);
            if (!Volatile.Read(ref _isDisposed))
            {
                ScreenshotStateChanged?.Invoke(this, nextState);
                RecentScreenshotsChanged?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }
        catch
        {
            // Never throw capture failures into UI callers; preserve previous state and history on failure.
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _captureInProgress, 0);
        }
    }

    public bool SelectRecent(int index)
    {
        if (Volatile.Read(ref _isDisposed) || index < 0)
        {
            return false;
        }

        ScreenshotState selectedState;
        bool stateChanged;

        lock (_stateGate)
        {
            if (_isDisposed || index >= _recentHistory.Count)
            {
                return false;
            }

            RecentScreenshot selected = _recentHistory[index];
            if (!selected.HasImageBytes)
            {
                return false;
            }

            // Do not reorder history; capture order remains capture order.
            selectedState = selected.ToScreenshotState();
            if (!selectedState.IsAvailable)
            {
                return false;
            }

            stateChanged = !_currentState.IsAvailable
                || !ReferenceEquals(_currentState.ImageData, selectedState.ImageData)
                || _currentState.CapturedAtUtc != selectedState.CapturedAtUtc;

            if (stateChanged)
            {
                _currentState = selectedState;
            }
        }

        if (stateChanged && !Volatile.Read(ref _isDisposed))
        {
            ScreenshotStateChanged?.Invoke(this, selectedState);
        }

        return true;
    }

    public void Clear()
    {
        bool stateChanged = false;
        bool historyChanged = false;

        lock (_stateGate)
        {
            if (_currentState.IsAvailable || _currentState.ImageData is not null)
            {
                _currentState = ScreenshotState.Empty;
                stateChanged = true;
            }

            if (_recentHistory.Count > 0)
            {
                _recentHistory.Clear();
                _recentSnapshot = Array.Empty<RecentScreenshot>();
                historyChanged = true;
            }
        }

        if (!Volatile.Read(ref _isDisposed))
        {
            if (stateChanged)
            {
                ScreenshotStateChanged?.Invoke(this, ScreenshotState.Empty);
            }

            if (historyChanged)
            {
                RecentScreenshotsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _isDisposed))
        {
            return;
        }

        Volatile.Write(ref _isDisposed, true);
        lock (_stateGate)
        {
            _currentState = ScreenshotState.Empty;
            _recentHistory.Clear();
            _recentSnapshot = Array.Empty<RecentScreenshot>();
        }

        ScreenshotStateChanged = null;
        RecentScreenshotsChanged = null;
    }

    private static bool TryGetPrimaryDisplayBounds(
        out int left,
        out int top,
        out int width,
        out int height)
    {
        nint hMonitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (hMonitor != nint.Zero)
        {
            MONITORINFO monitorInfo = new()
            {
                cbSize = Marshal.SizeOf<MONITORINFO>()
            };

            if (GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                left = monitorInfo.rcMonitor.Left;
                top = monitorInfo.rcMonitor.Top;
                width = monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left;
                height = monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top;

                if (width > 0 && height > 0)
                {
                    return true;
                }
            }
        }

        left = 0;
        top = 0;
        width = GetSystemMetrics(SM_CXSCREEN);
        height = GetSystemMetrics(SM_CYSCREEN);
        return width > 0 && height > 0;
    }

    /// <summary>
    /// Captures the primary display directly into a compact downscaled 32-bit top-down BGRA buffer
    /// so a full-resolution uncompressed desktop bitmap is never retained in managed memory.
    /// </summary>
    private static byte[]? TryCaptureDownscaledPrimaryDisplayBgra(
        int sourceLeft,
        int sourceTop,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        nint hdcScreen = GetDC(nint.Zero);
        if (hdcScreen == nint.Zero)
        {
            return null;
        }

        nint hdcMem = nint.Zero;
        nint hBitmap = nint.Zero;
        nint hOldObject = nint.Zero;

        try
        {
            hdcMem = CreateCompatibleDC(hdcScreen);
            if (hdcMem == nint.Zero)
            {
                return null;
            }

            BITMAPINFO bmi = new()
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = targetWidth,
                    // Negative height creates a top-down DIB matching BitmapEncoder BGRA row order.
                    biHeight = -targetHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                    biSizeImage = (uint)(targetWidth * targetHeight * 4)
                }
            };

            hBitmap = CreateDIBSection(
                hdcMem,
                ref bmi,
                DIB_RGB_COLORS,
                out nint bitsPtr,
                nint.Zero,
                0);

            if (hBitmap == nint.Zero || bitsPtr == nint.Zero)
            {
                return null;
            }

            hOldObject = SelectObject(hdcMem, hBitmap);
            if (hOldObject == nint.Zero)
            {
                return null;
            }

            SetStretchBltMode(hdcMem, HALFTONE);
            SetBrushOrgEx(hdcMem, 0, 0, out _);

            bool bltSucceeded = StretchBlt(
                hdcMem,
                0,
                0,
                targetWidth,
                targetHeight,
                hdcScreen,
                sourceLeft,
                sourceTop,
                sourceWidth,
                sourceHeight,
                SRCCOPY);

            if (!bltSucceeded)
            {
                return null;
            }

            int byteLength = checked(targetWidth * targetHeight * 4);
            byte[] pixelBytes = new byte[byteLength];
            Marshal.Copy(bitsPtr, pixelBytes, 0, byteLength);
            return pixelBytes;
        }
        finally
        {
            if (hOldObject != nint.Zero && hdcMem != nint.Zero)
            {
                SelectObject(hdcMem, hOldObject);
            }

            if (hBitmap != nint.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (hdcMem != nint.Zero)
            {
                DeleteDC(hdcMem);
            }

            ReleaseDC(nint.Zero, hdcScreen);
        }
    }

    private static async Task<byte[]?> TryEncodeBgraToPngAsync(
        byte[] bgraPixels,
        int width,
        int height)
    {
        using InMemoryRandomAccessStream memoryStream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memoryStream);

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            (uint)width,
            (uint)height,
            96.0,
            96.0,
            bgraPixels);

        await encoder.FlushAsync();

        ulong streamSize = memoryStream.Size;
        if (streamSize == 0 || streamSize > (ulong)DesignTokens.Screenshot.MaxEncodedImageBytes)
        {
            return null;
        }

        memoryStream.Seek(0);
        byte[] encodedBytes = new byte[(int)streamSize];
        using Stream inputStream = memoryStream.AsStreamForRead();
        int totalRead = 0;
        while (totalRead < encodedBytes.Length)
        {
            int read = await inputStream.ReadAsync(encodedBytes, totalRead, encodedBytes.Length - totalRead);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead == encodedBytes.Length ? encodedBytes : null;
    }

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
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(
        nint hdc,
        [In] ref BITMAPINFO pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(nint hdc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(nint hdc, int x, int y, out POINT lppt);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(
        nint hdcDest,
        int xDest,
        int yDest,
        int wDest,
        int hDest,
        nint hdcSrc,
        int xSrc,
        int ySrc,
        int wSrc,
        int hSrc,
        uint rop);
}
