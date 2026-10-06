using System;
using System.Runtime.InteropServices;
using System.Text;
using WindowsNotch.Core;

namespace WindowsNotch.Services.Clipboard;

/// <summary>
/// Event-driven Windows clipboard listener backed by <c>AddClipboardFormatListener</c> and <c>WM_CLIPBOARDUPDATE</c>.
/// Keeps only a single normalized preview in memory with zero polling, zero logging, and zero disk persistence.
/// </summary>
public sealed class WindowsClipboardService : IClipboardService
{
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_DIBV5 = 17;

    // Maximum UTF-16 code units inspected when building a 100-char normalized preview.
    private const int MaxInspectedCharCount = 1024;
    private const string ImagePreviewLabel = "Image";

    private readonly object _syncLock = new();
    private readonly uint _pngFormatId;

    private nint _registeredHwnd;
    private bool _isListenerRegistered;
    private bool _isRunning;
    private bool _isDisposed;
    private uint _lastSequenceNumber;

    public WindowsClipboardService()
    {
        try
        {
            _pngFormatId = RegisterClipboardFormatW("PNG");
        }
        catch
        {
            _pngFormatId = 0;
        }
    }

    public ClipboardState CurrentState { get; private set; } = ClipboardState.None;

    public event EventHandler<ClipboardState>? ClipboardStateChanged;

    public void Start(nint windowHandle)
    {
        lock (_syncLock)
        {
            if (_isDisposed || _isRunning || windowHandle == nint.Zero)
            {
                return;
            }

            try
            {
                if (AddClipboardFormatListener(windowHandle))
                {
                    _registeredHwnd = windowHandle;
                    _isListenerRegistered = true;
                }
            }
            catch
            {
                _isListenerRegistered = false;
            }

            // Record baseline sequence number at startup so stale pre-launch clipboard content
            // does not immediately override Calendar, DropPreview, or Screenshot indicators.
            // Only new clipboard updates (WM_CLIPBOARDUPDATE) during the app session are published.
            try
            {
                _lastSequenceNumber = GetClipboardSequenceNumber();
            }
            catch
            {
                _lastSequenceNumber = 0;
            }

            _isRunning = true;
        }
    }

    public void OnClipboardMessageReceived()
    {
        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning)
            {
                return;
            }
        }

        TryReadAndPublishClipboard(isInitialRead: false);
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            if (!_isRunning && !_isListenerRegistered)
            {
                return;
            }

            _isRunning = false;

            if (_isListenerRegistered && _registeredHwnd != nint.Zero)
            {
                try
                {
                    RemoveClipboardFormatListener(_registeredHwnd);
                }
                catch
                {
                    // HWND may already be destroyed during shutdown.
                }
            }

            _isListenerRegistered = false;
            _registeredHwnd = nint.Zero;
            _lastSequenceNumber = 0;
            CurrentState = ClipboardState.None;
        }
    }

    private void TryReadAndPublishClipboard(bool isInitialRead)
    {
        uint currentSequence = 0;
        try
        {
            currentSequence = GetClipboardSequenceNumber();
        }
        catch
        {
            currentSequence = 0;
        }

        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning)
            {
                return;
            }

            if (!isInitialRead && currentSequence != 0 && currentSequence == _lastSequenceNumber)
            {
                return;
            }
        }

        if (!TryCaptureClipboardSnapshot(out ClipboardContentType contentType, out string previewText, out byte[]? imageBytes))
        {
            // Another process currently holds the clipboard open; do not retry in a loop.
            return;
        }

        EventHandler<ClipboardState>? handler = null;
        ClipboardState? publishedState = null;

        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning)
            {
                return;
            }

            if (currentSequence != 0)
            {
                _lastSequenceNumber = currentSequence;
            }

            bool isAvailable = contentType is ClipboardContentType.Text or ClipboardContentType.Image;
            ClipboardState candidate = new(
                IsAvailable: isAvailable,
                ContentType: contentType,
                PreviewText: previewText,
                ImageData: imageBytes,
                UpdatedAt: DateTimeOffset.UtcNow);

            if (CurrentState.HasSameContentAs(candidate))
            {
                return;
            }

            CurrentState = candidate;
            publishedState = candidate;
            handler = ClipboardStateChanged;
        }

        if (handler is not null && publishedState is not null)
        {
            handler.Invoke(this, publishedState);
        }
    }

    private bool TryCaptureClipboardSnapshot(
        out ClipboardContentType contentType,
        out string previewText,
        out byte[]? imageBytes)
    {
        contentType = ClipboardContentType.None;
        previewText = string.Empty;
        imageBytes = null;

        bool hasUnicodeText = SafeIsFormatAvailable(CF_UNICODETEXT);
        bool hasPngImage = _pngFormatId != 0 && SafeIsFormatAvailable(_pngFormatId);
        bool hasBitmapImage = hasPngImage
            || SafeIsFormatAvailable(CF_DIBV5)
            || SafeIsFormatAvailable(CF_DIB)
            || SafeIsFormatAvailable(CF_BITMAP);

        if (!hasUnicodeText && !hasBitmapImage)
        {
            int formatCount = SafeCountClipboardFormats();
            contentType = formatCount > 0 ? ClipboardContentType.Unsupported : ClipboardContentType.None;
            return true;
        }

        bool opened = false;
        try
        {
            opened = OpenClipboard(nint.Zero);
            if (!opened)
            {
                return false;
            }

            if (hasUnicodeText && TryReadNormalizedUnicodeTextLocked(out string normalizedText))
            {
                if (string.IsNullOrEmpty(normalizedText))
                {
                    contentType = ClipboardContentType.None;
                    previewText = string.Empty;
                    return true;
                }

                contentType = ClipboardContentType.Text;
                previewText = normalizedText;
                return true;
            }

            if (hasBitmapImage)
            {
                contentType = ClipboardContentType.Image;
                previewText = ImagePreviewLabel;

                if (hasPngImage)
                {
                    imageBytes = TryReadCompactClipboardBytesLocked(_pngFormatId, DesignTokens.Clipboard.MaxCompactImageBytes);
                }

                return true;
            }

            contentType = ClipboardContentType.Unsupported;
            return true;
        }
        catch
        {
            contentType = ClipboardContentType.None;
            previewText = string.Empty;
            imageBytes = null;
            return false;
        }
        finally
        {
            if (opened)
            {
                try
                {
                    CloseClipboard();
                }
                catch
                {
                    // Ignore teardown errors when releasing clipboard lock.
                }
            }
        }
    }

    private static bool TryReadNormalizedUnicodeTextLocked(out string normalizedPreview)
    {
        normalizedPreview = string.Empty;

        nint handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == nint.Zero)
        {
            return false;
        }

        nuint byteSize = GlobalSize(handle);
        if (byteSize < sizeof(char))
        {
            return false;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return false;
        }

        try
        {
            int maxAvailableChars = (int)Math.Min(byteSize / sizeof(char), (nuint)MaxInspectedCharCount);
            if (maxAvailableChars <= 0)
            {
                return false;
            }

            int maxPreviewChars = DesignTokens.Clipboard.PreviewMaxCharacters;
            StringBuilder builder = new(Math.Min(maxAvailableChars, maxPreviewChars + 1));
            bool previousWasWhitespace = false;
            bool wasTruncated = false;

            for (int i = 0; i < maxAvailableChars; i++)
            {
                char ch = (char)Marshal.ReadInt16(pointer, i * sizeof(char));
                if (ch == '\0')
                {
                    break;
                }

                if (char.IsWhiteSpace(ch) || ch is '\r' or '\n' or '\t')
                {
                    if (builder.Length > 0)
                    {
                        previousWasWhitespace = true;
                    }

                    continue;
                }

                if (previousWasWhitespace)
                {
                    if (builder.Length >= maxPreviewChars)
                    {
                        wasTruncated = true;
                        break;
                    }

                    builder.Append(' ');
                    previousWasWhitespace = false;
                }

                if (builder.Length >= maxPreviewChars)
                {
                    wasTruncated = true;
                    break;
                }

                builder.Append(ch);
            }

            if (wasTruncated)
            {
                builder.Append('…');
            }

            normalizedPreview = builder.ToString();
            return true;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? TryReadCompactClipboardBytesLocked(uint formatId, int maxByteLength)
    {
        nint handle = GetClipboardData(formatId);
        if (handle == nint.Zero)
        {
            return null;
        }

        nuint size = GlobalSize(handle);
        if (size == 0 || size > (nuint)maxByteLength)
        {
            return null;
        }

        nint pointer = GlobalLock(handle);
        if (pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            int length = (int)size;
            byte[] buffer = new byte[length];
            Marshal.Copy(pointer, buffer, 0, length);
            return buffer;
        }
        catch
        {
            return null;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool SafeIsFormatAvailable(uint format)
    {
        try
        {
            return IsClipboardFormatAvailable(format);
        }
        catch
        {
            return false;
        }
    }

    private static int SafeCountClipboardFormats()
    {
        try
        {
            return CountClipboardFormats();
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            Stop();
            _isDisposed = true;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint hMem);
}
