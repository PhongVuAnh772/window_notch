namespace WindowsNotch.Notch;

/// <summary>
/// Represents the lifecycle states of the persistent Notch surface.
/// <see cref="Idle"/>, <see cref="Hover"/>, <see cref="Media"/>, <see cref="Clipboard"/>, <see cref="Screenshot"/>, <see cref="ScreenshotHistory"/>, <see cref="DropTarget"/>, <see cref="Calendar"/>, and <see cref="Expanded"/> are active in Step 13;
/// remaining values are reserved for future milestones.
/// </summary>
public enum NotchState
{
    Idle = 0,
    Hover,
    Media,
    Clipboard,
    Screenshot,
    ScreenshotHistory,
    DropTarget,
    Calendar,
    Timer,
    Expanded
}

