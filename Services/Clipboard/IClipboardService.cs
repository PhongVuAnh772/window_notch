using System;

namespace WindowsNotch.Services.Clipboard;

/// <summary>
/// Event-driven native system clipboard service contract.
/// </summary>
public interface IClipboardService : IDisposable
{
    /// <summary>
    /// Gets the latest in-memory clipboard state snapshot.
    /// </summary>
    ClipboardState CurrentState { get; }

    /// <summary>
    /// Raised when the system clipboard content meaningfully changes.
    /// </summary>
    event EventHandler<ClipboardState>? ClipboardStateChanged;

    /// <summary>
    /// Registers the native clipboard format listener on the specified HWND and performs one initial read.
    /// </summary>
    void Start(nint windowHandle);

    /// <summary>
    /// Handles a native WM_CLIPBOARDUPDATE window message notification.
    /// </summary>
    void OnClipboardMessageReceived();

    /// <summary>
    /// Unregisters the native clipboard format listener and clears in-memory clipboard data.
    /// </summary>
    void Stop();
}
