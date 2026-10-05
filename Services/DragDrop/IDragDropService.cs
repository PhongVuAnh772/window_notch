using System;

namespace WindowsNotch.Services.DragDrop;

/// <summary>
/// UI-independent contract for native Windows drag-and-drop registration and lightweight file metadata extraction.
/// </summary>
public interface IDragDropService : IDisposable
{
    DragDropState CurrentState { get; }

    event EventHandler<DragDropState>? DragDropStateChanged;

    void Start(nint windowHandle);

    void Start(
        nint windowHandle,
        Func<int, int, bool>? hitTestInteractiveBounds,
        Func<bool>? canAcceptDrag);

    void Stop();

    void Clear();
}
