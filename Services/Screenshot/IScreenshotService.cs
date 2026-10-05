using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WindowsNotch.Services.Screenshot;

/// <summary>
/// UI-independent contract for capturing the primary Windows display into a compact in-memory preview snapshot
/// and maintaining an immutable in-memory recent history of up to 3 screenshots.
/// </summary>
public interface IScreenshotService : IDisposable
{
    ScreenshotState CurrentState { get; }

    IReadOnlyList<RecentScreenshot> RecentScreenshots { get; }

    event EventHandler<ScreenshotState>? ScreenshotStateChanged;

    event EventHandler? RecentScreenshotsChanged;

    Task<bool> CapturePrimaryDisplayAsync();

    bool SelectRecent(int index);

    void Clear();
}

