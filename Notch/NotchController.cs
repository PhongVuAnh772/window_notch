using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WindowsNotch.Core;
using WindowsNotch.Services.Calendar;
using WindowsNotch.Services.Clipboard;
using WindowsNotch.Services.DragDrop;
using WindowsNotch.Services.Hotkeys;
using WindowsNotch.Services.Media;
using WindowsNotch.Services.Screenshot;
using WindowsNotch.Services.Time;
using WindowsNotch.Services.Tray;

namespace WindowsNotch.Notch;

public readonly record struct NotchDimensions(
    double LogicalWidth,
    double LogicalHeight,
    double CornerRadius,
    float ContentScale,
    float ContentOpacity,
    float SurfaceOpacity,
    float InnerDepthOpacity,
    float BorderOpacity,
    float ShadowBlurRadius,
    float ShadowOpacity,
    float ShadowOffsetY)
{
    public double HostCanvasWidth => DesignTokens.Canvas.HostWidth;

    public double HostCanvasHeight => DesignTokens.Canvas.HostHeight;
}

public sealed class NotchStateChangedEventArgs : EventArgs
{
    public NotchStateChangedEventArgs(
        NotchState previousState,
        NotchState currentState,
        NotchDimensions targetDimensions)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        TargetDimensions = targetDimensions;
    }

    public NotchState PreviousState { get; }

    public NotchState CurrentState { get; }

    public NotchDimensions TargetDimensions { get; }

    public bool IsGeometryTransition =>
        !(NotchController.IsCollapsedState(PreviousState) && NotchController.IsCollapsedState(CurrentState));
}

/// <summary>
/// Coordinates Notch state transitions (Idle, Media, Clipboard, Screenshot, ScreenshotHistory, DropTarget, Calendar, Hover, Expanded),
/// resolves surface geometry and visual tokens from centralized design tokens, manages the cancellable mouse-leave grace period,
/// and bridges the event-driven clock, system media, native clipboard, primary-display screenshot, global hotkey, system tray, native drag-and-drop, and calendar services.
/// </summary>
public sealed class NotchController : IDisposable
{
    private readonly IClockService _clockService;
    private readonly IMediaService _mediaService;
    private readonly IClipboardService _clipboardService;
    private readonly IScreenshotService _screenshotService;
    private readonly IDragDropService _dragDropService;
    private readonly ICalendarService _calendarService;
    private readonly IGlobalHotkeyService _globalHotkeyService;
    private readonly ISystemTrayService _systemTrayService;
    private CancellationTokenSource? _collapseDelayCts;
    private DragDropState _lastCommittedDropState = DragDropState.Empty;
    private bool _isPointerHovered;
    private bool _isUserScreenshotSessionActive;
    private bool _isDisposed;

    public NotchController()
        : this(
            new SystemClockService(),
            new WindowsMediaService(),
            new WindowsClipboardService(),
            new WindowsScreenshotService(),
            new WindowsDragDropService(),
            new WindowsCalendarService(),
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(IClockService clockService)
        : this(
            clockService,
            new WindowsMediaService(),
            new WindowsClipboardService(),
            new WindowsScreenshotService(),
            new WindowsDragDropService(),
            new WindowsCalendarService(),
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(IClockService clockService, IMediaService mediaService)
        : this(
            clockService,
            mediaService,
            new WindowsClipboardService(),
            new WindowsScreenshotService(),
            new WindowsDragDropService(),
            new WindowsCalendarService(),
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(
        IClockService clockService,
        IMediaService mediaService,
        IClipboardService clipboardService)
        : this(
            clockService,
            mediaService,
            clipboardService,
            new WindowsScreenshotService(),
            new WindowsDragDropService(),
            new WindowsCalendarService(),
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(
        IClockService clockService,
        IMediaService mediaService,
        IClipboardService clipboardService,
        IScreenshotService screenshotService)
        : this(
            clockService,
            mediaService,
            clipboardService,
            screenshotService,
            new WindowsDragDropService(),
            new WindowsCalendarService(),
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(
        IClockService clockService,
        IMediaService mediaService,
        IClipboardService clipboardService,
        IScreenshotService screenshotService,
        IDragDropService dragDropService)
        : this(
            clockService,
            mediaService,
            clipboardService,
            screenshotService,
            dragDropService,
            new WindowsCalendarService(),
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(
        IClockService clockService,
        IMediaService mediaService,
        IClipboardService clipboardService,
        IScreenshotService screenshotService,
        IDragDropService dragDropService,
        ICalendarService calendarService)
        : this(
            clockService,
            mediaService,
            clipboardService,
            screenshotService,
            dragDropService,
            calendarService,
            new WindowsGlobalHotkeyService(),
            new WindowsSystemTrayService())
    {
    }

    public NotchController(
        IClockService clockService,
        IMediaService mediaService,
        IClipboardService clipboardService,
        IScreenshotService screenshotService,
        IDragDropService dragDropService,
        ICalendarService calendarService,
        IGlobalHotkeyService globalHotkeyService)
        : this(
            clockService,
            mediaService,
            clipboardService,
            screenshotService,
            dragDropService,
            calendarService,
            globalHotkeyService,
            new WindowsSystemTrayService())
    {
    }

    public NotchController(
        IClockService clockService,
        IMediaService mediaService,
        IClipboardService clipboardService,
        IScreenshotService screenshotService,
        IDragDropService dragDropService,
        ICalendarService calendarService,
        IGlobalHotkeyService globalHotkeyService,
        ISystemTrayService systemTrayService)
    {
        ArgumentNullException.ThrowIfNull(clockService);
        ArgumentNullException.ThrowIfNull(mediaService);
        ArgumentNullException.ThrowIfNull(clipboardService);
        ArgumentNullException.ThrowIfNull(screenshotService);
        ArgumentNullException.ThrowIfNull(dragDropService);
        ArgumentNullException.ThrowIfNull(calendarService);
        ArgumentNullException.ThrowIfNull(globalHotkeyService);
        ArgumentNullException.ThrowIfNull(systemTrayService);

        _clockService = clockService;
        _mediaService = mediaService;
        _clipboardService = clipboardService;
        _screenshotService = screenshotService;
        _dragDropService = dragDropService;
        _calendarService = calendarService;
        _globalHotkeyService = globalHotkeyService;
        _systemTrayService = systemTrayService;

        _clockService.TimeChanged += OnClockServiceTimeChanged;
        _mediaService.MediaStateChanged += OnMediaServiceStateChanged;
        _clipboardService.ClipboardStateChanged += OnClipboardServiceStateChanged;
        _screenshotService.ScreenshotStateChanged += OnScreenshotServiceStateChanged;
        _screenshotService.RecentScreenshotsChanged += OnScreenshotServiceRecentChanged;
        _dragDropService.DragDropStateChanged += OnDragDropServiceStateChanged;
        _calendarService.CalendarStateChanged += OnCalendarServiceStateChanged;
        _globalHotkeyService.ScreenshotHotkeyPressed += OnGlobalScreenshotHotkeyPressed;
        _systemTrayService.ShowRequested += OnSystemTrayShowRequested;
        _systemTrayService.HideRequested += OnSystemTrayHideRequested;
        _systemTrayService.ExitRequested += OnSystemTrayExitRequested;

        _clockService.Start();
        _mediaService.Start();
        _calendarService.Start();

        CurrentState = ResolveCollapsedRestState();
    }

    public NotchState CurrentState { get; private set; } = NotchState.Idle;

    /// <summary>
    /// Application/window-level visibility state of the Notch shell.
    /// Distinct from <see cref="NotchState"/> so hiding the Notch never destroys state or stops background services.
    /// </summary>
    public bool IsNotchVisible { get; private set; } = true;

    public string CurrentTimeText => _clockService.CurrentTimeText;

    public MediaState CurrentMediaState => _mediaService.CurrentState;

    public ClipboardState CurrentClipboardState => _clipboardService.CurrentState;

    public ScreenshotState CurrentScreenshotState => _screenshotService.CurrentState;

    public IReadOnlyList<RecentScreenshot> RecentScreenshots => _screenshotService.RecentScreenshots;

    public DragDropState CurrentDragDropState => _dragDropService.CurrentState;

    public CalendarState CurrentCalendarState => _calendarService.CurrentState;

    public bool IsScreenshotHotkeyRegistered =>
        !_isDisposed && _globalHotkeyService.IsRegistered;

    public bool ScreenshotHotkeyRegistrationFailed =>
        _globalHotkeyService.HasRegistrationFailed;

    public bool IsSystemTrayAvailable =>
        !_isDisposed && _systemTrayService.IsAvailable;

    public bool SystemTrayRegistrationFailed =>
        _systemTrayService.HasRegistrationFailed;

    public bool HasActiveMedia =>
        CurrentMediaState.IsAvailable && CurrentMediaState.Track is not null;

    public bool HasActiveClipboard =>
        CurrentClipboardState.IsAvailable;

    public bool HasActiveScreenshot =>
        CurrentScreenshotState.IsAvailable && CurrentScreenshotState.HasImageBytes;

    public bool HasRecentScreenshots =>
        RecentScreenshots.Count > 0;

    public bool HasActiveDrag =>
        CurrentDragDropState.IsDragging;

    public bool HasActiveDropPreview =>
        CurrentDragDropState.HasDropPreview && !string.IsNullOrEmpty(CurrentDragDropState.FileName);

    public bool HasActiveCalendarEvent =>
        CurrentCalendarState.HasUpcomingEvent;

    /// <summary>
    /// True when the Notch is allowed to accept an external file drag/drop interaction.
    /// Protects Media Expanded, ScreenshotHistory, and hidden state so dragging over them never interrupts or accepts drops.
    /// </summary>
    public bool CanAcceptDrag =>
        !_isDisposed &&
        IsNotchVisible &&
        CurrentState is not (NotchState.Expanded or NotchState.ScreenshotHistory);

    /// <summary>
    /// True when the Hover view should display the Screenshot preview:
    /// requires no active media, an available screenshot, and either no active clipboard
    /// or an explicit user-triggered screenshot interaction during the current hover session.
    /// </summary>
    public bool ShouldShowScreenshotInHover =>
        !HasActiveMedia &&
        HasActiveScreenshot &&
        (!HasActiveClipboard || _isUserScreenshotSessionActive);

    public bool IsPointerHovered => _isPointerHovered;

    public event EventHandler<NotchStateChangedEventArgs>? StateChanged;

    public event EventHandler<bool>? NotchVisibilityChanged;

    public event EventHandler? ApplicationExitRequested;

    public event EventHandler<string>? TimeTextChanged;

    public event EventHandler<MediaState>? MediaStateChanged;

    public event EventHandler<ClipboardState>? ClipboardStateChanged;

    public event EventHandler<ScreenshotState>? ScreenshotStateChanged;

    public event EventHandler? RecentScreenshotsChanged;

    public event EventHandler<DragDropState>? DragDropStateChanged;

    public event EventHandler<CalendarState>? CalendarStateChanged;

    public static bool IsCollapsedState(NotchState state)
    {
        return state is
            NotchState.Idle or
            NotchState.Media or
            NotchState.Clipboard or
            NotchState.Screenshot or
            NotchState.Calendar;
    }

    public NotchDimensions GetDimensionsForCurrentState()
    {
        return GetDimensions(CurrentState);
    }

    public static NotchDimensions GetDimensions(NotchState state)
    {
        return state switch
        {
            NotchState.Idle or
            NotchState.Media or
            NotchState.Clipboard or
            NotchState.Screenshot or
            NotchState.Calendar => CreateIdleDimensions(),

            NotchState.Hover => new NotchDimensions(
                DesignTokens.NotchSize.HoverWidth,
                DesignTokens.NotchSize.HoverHeight,
                DesignTokens.Radius.Pill(DesignTokens.NotchSize.HoverHeight),
                DesignTokens.Animation.HoverContentScale,
                DesignTokens.Animation.HoverContentOpacity,
                DesignTokens.Surface.HoverSurfaceOpacity,
                DesignTokens.Surface.HoverInnerDepthOpacity,
                DesignTokens.Surface.HoverBorderOpacity,
                DesignTokens.Surface.HoverShadowBlurRadius,
                DesignTokens.Surface.HoverShadowOpacity,
                DesignTokens.Surface.HoverShadowOffsetY),

            NotchState.DropTarget => new NotchDimensions(
                DesignTokens.DropTarget.Width,
                DesignTokens.DropTarget.Height,
                DesignTokens.DropTarget.CornerRadius,
                DesignTokens.Animation.ExpandedContentScale,
                DesignTokens.Animation.ExpandedContentOpacity,
                DesignTokens.Surface.ExpandedSurfaceOpacity,
                DesignTokens.Surface.ExpandedInnerDepthOpacity,
                DesignTokens.Surface.ExpandedBorderOpacity,
                DesignTokens.Surface.ExpandedShadowBlurRadius,
                DesignTokens.Surface.ExpandedShadowOpacity,
                DesignTokens.Surface.ExpandedShadowOffsetY),

            NotchState.ScreenshotHistory => new NotchDimensions(
                DesignTokens.Screenshot.HistoryWidth,
                DesignTokens.Screenshot.HistoryHeight,
                DesignTokens.Screenshot.HistoryCornerRadius,
                DesignTokens.Animation.ExpandedContentScale,
                DesignTokens.Animation.ExpandedContentOpacity,
                DesignTokens.Surface.ExpandedSurfaceOpacity,
                DesignTokens.Surface.ExpandedInnerDepthOpacity,
                DesignTokens.Surface.ExpandedBorderOpacity,
                DesignTokens.Surface.ExpandedShadowBlurRadius,
                DesignTokens.Surface.ExpandedShadowOpacity,
                DesignTokens.Surface.ExpandedShadowOffsetY),

            NotchState.Expanded => new NotchDimensions(
                DesignTokens.NotchSize.MediaExpandedWidth,
                DesignTokens.NotchSize.MediaExpandedHeight,
                DesignTokens.Radius.R24,
                DesignTokens.Animation.ExpandedContentScale,
                DesignTokens.Animation.ExpandedContentOpacity,
                DesignTokens.Surface.ExpandedSurfaceOpacity,
                DesignTokens.Surface.ExpandedInnerDepthOpacity,
                DesignTokens.Surface.ExpandedBorderOpacity,
                DesignTokens.Surface.ExpandedShadowBlurRadius,
                DesignTokens.Surface.ExpandedShadowOpacity,
                DesignTokens.Surface.ExpandedShadowOffsetY),

            // Future states remain inactive in Step 13 and fall back to collapsed Idle geometry.
            _ => CreateIdleDimensions()
        };
    }

    private static NotchDimensions CreateIdleDimensions()
    {
        return new NotchDimensions(
            DesignTokens.NotchSize.CollapsedWidth,
            DesignTokens.NotchSize.CollapsedHeight,
            DesignTokens.Radius.Pill(DesignTokens.NotchSize.CollapsedHeight),
            DesignTokens.Animation.IdleContentScale,
            DesignTokens.Animation.IdleContentOpacity,
            DesignTokens.Surface.IdleSurfaceOpacity,
            DesignTokens.Surface.IdleInnerDepthOpacity,
            DesignTokens.Surface.IdleBorderOpacity,
            DesignTokens.Surface.IdleShadowBlurRadius,
            DesignTokens.Surface.IdleShadowOpacity,
            DesignTokens.Surface.IdleShadowOffsetY);
    }

    public bool OnPointerEntered()
    {
        if (_isDisposed || !IsNotchVisible)
        {
            return false;
        }

        _isPointerHovered = true;
        CancelPendingCollapse();

        // Trigger a throttled background calendar check on user interaction without blocking hover transitions.
        _ = RequestCalendarRefresh();

        if (CurrentState is NotchState.Expanded or NotchState.DropTarget or NotchState.ScreenshotHistory)
        {
            return false;
        }

        // If a dropped file preview is available and no higher-priority Media, Clipboard, or Screenshot is active,
        // hovering opens the DropTarget preview surface (300x64).
        if (!HasActiveMedia && !HasActiveClipboard && !HasActiveScreenshot && HasActiveDropPreview)
        {
            return TryTransitionTo(NotchState.DropTarget);
        }

        return TryTransitionTo(NotchState.Hover);
    }

    public void OnPointerMovedWithinNotch()
    {
        if (_isDisposed || !IsNotchVisible)
        {
            return;
        }

        _isPointerHovered = true;
        CancelPendingCollapse();
    }

    /// <summary>
    /// Explicitly expands the media panel when the user clicks the media metadata/artwork area in Hover state.
    /// </summary>
    public bool RequestExpandMedia()
    {
        if (_isDisposed || !IsNotchVisible || !HasActiveMedia || CurrentState != NotchState.Hover)
        {
            return false;
        }

        _isPointerHovered = true;
        CancelPendingCollapse();
        return TryTransitionTo(NotchState.Expanded);
    }

    /// <summary>
    /// Explicitly expands the Notch into the ScreenshotHistory surface (380x120) when the user clicks
    /// the "Recent screenshots" affordance in the Screenshot Hover preview.
    /// Never interrupts Media Expanded or DropTarget.
    /// </summary>
    public bool RequestOpenScreenshotHistory()
    {
        if (_isDisposed ||
            !IsNotchVisible ||
            CurrentState is NotchState.Expanded or NotchState.DropTarget ||
            !HasActiveScreenshot ||
            !HasRecentScreenshots)
        {
            return false;
        }

        _isPointerHovered = true;
        _isUserScreenshotSessionActive = true;
        CancelPendingCollapse();
        return TryTransitionTo(NotchState.ScreenshotHistory);
    }

    /// <summary>
    /// Synchronously selects a recent screenshot by index (0..2) so it becomes the active ScreenshotState preview
    /// without reordering capture history or performing disk/clipboard operations, and returns smoothly to Hover (220x44).
    /// </summary>
    public bool SelectRecentScreenshot(int index)
    {
        if (_isDisposed)
        {
            return false;
        }

        bool selected = _screenshotService.SelectRecent(index);
        if (!selected)
        {
            return false;
        }

        _isUserScreenshotSessionActive = true;
        CancelPendingCollapse();

        if (CurrentState == NotchState.ScreenshotHistory)
        {
            TryTransitionTo(NotchState.Hover);
        }

        return true;
    }

    /// <summary>
    /// Dismisses the ScreenshotHistory view (e.g., on Escape key) back to Hover (if pointer is inside) or collapsed rest state.
    /// </summary>
    public bool DismissScreenshotHistory(Func<bool>? isPointerStillInsideCheck = null)
    {
        if (_isDisposed || CurrentState != NotchState.ScreenshotHistory)
        {
            return false;
        }

        CancelPendingCollapse();

        if (isPointerStillInsideCheck is not null && isPointerStillInsideCheck())
        {
            _isPointerHovered = true;
            return TryTransitionTo(NotchState.Hover);
        }

        _isPointerHovered = false;
        _isUserScreenshotSessionActive = false;
        return TryTransitionTo(ResolveCollapsedRestState());
    }

    public async void OnPointerExited(Func<bool>? isPointerStillInsideCheck = null)
    {
        if (_isDisposed || HasActiveDrag || IsCollapsedState(CurrentState))
        {
            return;
        }

        if (isPointerStillInsideCheck is not null && isPointerStillInsideCheck())
        {
            return;
        }

        CancelPendingCollapse();

        // When leaving Expanded media, return to Hover first, then apply the 300ms leave grace period.
        if (CurrentState == NotchState.Expanded)
        {
            TryTransitionTo(NotchState.Hover);
        }

        CancellationTokenSource cts = new();
        _collapseDelayCts = cts;
        CancellationToken token = cts.Token;

        try
        {
            await Task.Delay(DesignTokens.Animation.MouseLeaveGraceDelayMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (_isDisposed || token.IsCancellationRequested || HasActiveDrag)
        {
            return;
        }

        if (isPointerStillInsideCheck is not null && isPointerStillInsideCheck())
        {
            return;
        }

        _isPointerHovered = false;
        _isUserScreenshotSessionActive = false;
        TryTransitionTo(ResolveCollapsedRestState());
    }

    public void CancelPendingCollapse()
    {
        if (_collapseDelayCts is null)
        {
            return;
        }

        _collapseDelayCts.Cancel();
        _collapseDelayCts.Dispose();
        _collapseDelayCts = null;
    }

    public Task<bool> PlayPauseMediaAsync()
    {
        if (_isDisposed || !HasActiveMedia)
        {
            return Task.FromResult(false);
        }

        return _mediaService.TogglePlayPauseAsync();
    }

    public Task<bool> PlayMediaAsync()
    {
        if (_isDisposed || !HasActiveMedia)
        {
            return Task.FromResult(false);
        }

        return _mediaService.PlayAsync();
    }

    public Task<bool> PauseMediaAsync()
    {
        if (_isDisposed || !HasActiveMedia)
        {
            return Task.FromResult(false);
        }

        return _mediaService.PauseAsync();
    }

    public Task<bool> SkipNextMediaAsync()
    {
        if (_isDisposed || !HasActiveMedia)
        {
            return Task.FromResult(false);
        }

        return _mediaService.SkipNextAsync();
    }

    public Task<bool> SkipPreviousMediaAsync()
    {
        if (_isDisposed || !HasActiveMedia)
        {
            return Task.FromResult(false);
        }

        return _mediaService.SkipPreviousAsync();
    }

    public Task<bool> SeekMediaAsync(TimeSpan requestedPosition)
    {
        if (_isDisposed || !HasActiveMedia)
        {
            return Task.FromResult(false);
        }

        return _mediaService.SeekAsync(requestedPosition);
    }

    /// <summary>
    /// Deterministic controller-owned screenshot capture trigger.
    /// Never interrupts Media Expanded or DropTarget and delegates capture to <see cref="IScreenshotService.CapturePrimaryDisplayAsync"/>.
    /// Works even while the Notch window is hidden via the System Tray without automatically unhiding the Notch.
    /// </summary>
    public Task<bool> RequestScreenshotCapture()
    {
        return RequestScreenshotCaptureAsync();
    }

    public async Task<bool> RequestScreenshotCaptureAsync()
    {
        if (_isDisposed ||
            CurrentState is NotchState.Expanded or NotchState.DropTarget ||
            HasActiveDrag)
        {
            return false;
        }

        _isUserScreenshotSessionActive = true;
        bool captured = await _screenshotService.CapturePrimaryDisplayAsync();
        if (!captured && !HasActiveScreenshot)
        {
            _isUserScreenshotSessionActive = false;
        }

        return captured;
    }

    public void ClearScreenshot()
    {
        if (_isDisposed)
        {
            return;
        }

        _isUserScreenshotSessionActive = false;
        _screenshotService.Clear();

        if (CurrentState == NotchState.ScreenshotHistory)
        {
            TryTransitionTo(_isPointerHovered ? NotchState.Hover : ResolveCollapsedRestState());
        }
    }

    public void StartGlobalHotkeyMonitoring(nint windowHandle)
    {
        if (_isDisposed)
        {
            return;
        }

        _globalHotkeyService.Start(windowHandle);
    }

    public void OnNativeHotkeyPressed(int hotkeyId)
    {
        if (_isDisposed)
        {
            return;
        }

        _globalHotkeyService.OnHotkeyMessageReceived(hotkeyId);
    }

    public void StartSystemTrayMonitoring(nint windowHandle)
    {
        if (_isDisposed)
        {
            return;
        }

        _systemTrayService.Start(windowHandle);
        _systemTrayService.UpdateNotchVisibility(IsNotchVisible);
    }

    public void OnNativeTrayMessageReceived(nuint wParam, nint lParam)
    {
        if (_isDisposed)
        {
            return;
        }

        _systemTrayService.OnTrayMessageReceived(wParam, lParam, IsNotchVisible);
    }

    /// <summary>
    /// Shows the existing Notch window without resetting application state or recreating services.
    /// </summary>
    public bool ShowNotch()
    {
        if (_isDisposed || IsNotchVisible)
        {
            return false;
        }

        IsNotchVisible = true;
        _isPointerHovered = false;
        CancelPendingCollapse();
        TryTransitionTo(ResolveCollapsedRestState());
        _systemTrayService.UpdateNotchVisibility(true);
        NotchVisibilityChanged?.Invoke(this, true);
        return true;
    }

    /// <summary>
    /// Hides the existing Notch window while preserving all background services, in-memory history, and the global screenshot hotkey.
    /// </summary>
    public bool HideNotch()
    {
        if (_isDisposed || !IsNotchVisible)
        {
            return false;
        }

        IsNotchVisible = false;
        _isPointerHovered = false;
        _isUserScreenshotSessionActive = false;
        CancelPendingCollapse();
        TryTransitionTo(ResolveCollapsedRestState());
        _systemTrayService.UpdateNotchVisibility(false);
        NotchVisibilityChanged?.Invoke(this, false);
        return true;
    }

    public void RequestApplicationExit()
    {
        if (_isDisposed)
        {
            return;
        }

        ApplicationExitRequested?.Invoke(this, EventArgs.Empty);
    }

    public void StartClipboardMonitoring(nint windowHandle)
    {
        if (_isDisposed)
        {
            return;
        }

        _clipboardService.Start(windowHandle);

        if (!_isPointerHovered && IsCollapsedState(CurrentState))
        {
            TryTransitionTo(ResolveCollapsedRestState());
        }
    }

    public void OnClipboardMessageReceived()
    {
        if (_isDisposed)
        {
            return;
        }

        _clipboardService.OnClipboardMessageReceived();
    }

    public void StartDragDropMonitoring(
        nint windowHandle,
        Func<int, int, bool>? hitTestInteractiveBounds = null)
    {
        if (_isDisposed)
        {
            return;
        }

        _dragDropService.Start(
            windowHandle,
            hitTestInteractiveBounds: hitTestInteractiveBounds,
            canAcceptDrag: () => CanAcceptDrag);
    }

    public bool OnDragEnter()
    {
        if (!CanAcceptDrag)
        {
            return false;
        }

        CancelPendingCollapse();
        return TryTransitionTo(NotchState.DropTarget);
    }

    public void OnDragLeave(Func<bool>? isPointerStillInsideCheck = null)
    {
        if (_isDisposed || CurrentState != NotchState.DropTarget)
        {
            return;
        }

        CancelPendingCollapse();

        bool pointerInside = isPointerStillInsideCheck?.Invoke() ?? _isPointerHovered;
        if (pointerInside)
        {
            _isPointerHovered = true;
            if (!HasActiveMedia && !HasActiveClipboard && !HasActiveScreenshot && HasActiveDropPreview)
            {
                return;
            }

            TryTransitionTo(NotchState.Hover);
        }
        else
        {
            _isPointerHovered = false;
            _isUserScreenshotSessionActive = false;
            TryTransitionTo(ResolveCollapsedRestState());
        }
    }

    public void OnFileDropped(DragDropState dropState)
    {
        if (!CanAcceptDrag || !dropState.HasDropPreview)
        {
            return;
        }

        _isPointerHovered = true;
        _isUserScreenshotSessionActive = false;
        CancelPendingCollapse();
        TryTransitionTo(NotchState.DropTarget);
    }

    public void ClearDropPreview()
    {
        if (_isDisposed)
        {
            return;
        }

        _dragDropService.Clear();
    }

    public Task RequestCalendarRefresh()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        return _calendarService.RefreshAsync();
    }

    public void OnCalendarStateChanged(CalendarState newCalendarState)
    {
        if (_isDisposed)
        {
            return;
        }

        // Calendar updates never interrupt Expanded media, ScreenshotHistory, active Drag & Drop, or active pointer hover.
        if (!_isPointerHovered && !HasActiveDrag && IsCollapsedState(CurrentState))
        {
            TryTransitionTo(ResolveCollapsedRestState());
        }

        CalendarStateChanged?.Invoke(this, newCalendarState);
    }

    public bool TryTransitionTo(NotchState nextState)
    {
        if (_isDisposed)
        {
            return false;
        }

        // Only Idle, Media, Clipboard, Screenshot, ScreenshotHistory, DropTarget, Calendar, Hover, and Expanded are active in Step 13.
        if (nextState is not (
            NotchState.Idle or
            NotchState.Media or
            NotchState.Clipboard or
            NotchState.Screenshot or
            NotchState.ScreenshotHistory or
            NotchState.DropTarget or
            NotchState.Calendar or
            NotchState.Hover or
            NotchState.Expanded))
        {
            return false;
        }

        // Expanded requires active media.
        if (nextState == NotchState.Expanded && !HasActiveMedia)
        {
            return false;
        }

        // ScreenshotHistory requires an active screenshot and non-empty recent history.
        if (nextState == NotchState.ScreenshotHistory && (!HasActiveScreenshot || !HasRecentScreenshots))
        {
            return false;
        }

        // Idempotent guard: do not restart transition if already in the target state.
        if (CurrentState == nextState)
        {
            return false;
        }

        NotchState previousState = CurrentState;
        CurrentState = nextState;
        NotchDimensions targetDimensions = GetDimensions(nextState);

        StateChanged?.Invoke(
            this,
            new NotchStateChangedEventArgs(previousState, nextState, targetDimensions));

        return true;
    }

    private NotchState ResolveCollapsedRestState()
    {
        if (HasActiveMedia)
        {
            return NotchState.Media;
        }

        if (HasActiveClipboard)
        {
            return NotchState.Clipboard;
        }

        if (HasActiveScreenshot)
        {
            return NotchState.Screenshot;
        }

        if (HasActiveCalendarEvent)
        {
            return NotchState.Calendar;
        }

        return NotchState.Idle;
    }

    private void OnClockServiceTimeChanged(object? sender, string newTimeText)
    {
        if (_isDisposed)
        {
            return;
        }

        // Clock updates only notify content subscribers and never modify NotchState.
        TimeTextChanged?.Invoke(this, newTimeText);
    }

    private void OnMediaServiceStateChanged(object? sender, MediaState newMediaState)
    {
        if (_isDisposed)
        {
            return;
        }

        // Media updates must never interrupt ScreenshotHistory.
        if (CurrentState != NotchState.ScreenshotHistory)
        {
            if (HasActiveMedia)
            {
                _isUserScreenshotSessionActive = false;
            }

            if (!HasActiveMedia && CurrentState == NotchState.Expanded)
            {
                TryTransitionTo(_isPointerHovered ? NotchState.Hover : ResolveCollapsedRestState());
            }
            else if (!_isPointerHovered && !HasActiveDrag && CurrentState != NotchState.DropTarget)
            {
                TryTransitionTo(ResolveCollapsedRestState());
            }
        }

        MediaStateChanged?.Invoke(this, newMediaState);
    }

    private void OnClipboardServiceStateChanged(object? sender, ClipboardState newClipboardState)
    {
        if (_isDisposed)
        {
            return;
        }

        // Clipboard changes never interrupt ScreenshotHistory, active pointer hover, active drag/drop, or Expanded media.
        if (newClipboardState.IsAvailable && CurrentState != NotchState.ScreenshotHistory)
        {
            // A fresh clipboard copy returns hover priority to Clipboard unless the user triggers another screenshot.
            _isUserScreenshotSessionActive = false;
        }

        if (!_isPointerHovered && !HasActiveDrag && IsCollapsedState(CurrentState))
        {
            TryTransitionTo(ResolveCollapsedRestState());
        }

        ClipboardStateChanged?.Invoke(this, newClipboardState);
    }

    private void OnScreenshotServiceStateChanged(object? sender, ScreenshotState newScreenshotState)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!newScreenshotState.IsAvailable)
        {
            _isUserScreenshotSessionActive = false;
        }

        // Screenshot updates never interrupt Expanded media, ScreenshotHistory, active drag/drop, or force-open the Notch.
        if (!_isPointerHovered && !HasActiveDrag && IsCollapsedState(CurrentState))
        {
            TryTransitionTo(ResolveCollapsedRestState());
        }

        ScreenshotStateChanged?.Invoke(this, newScreenshotState);
    }

    private void OnScreenshotServiceRecentChanged(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (!HasRecentScreenshots && CurrentState == NotchState.ScreenshotHistory)
        {
            TryTransitionTo(_isPointerHovered ? NotchState.Hover : ResolveCollapsedRestState());
        }

        RecentScreenshotsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDragDropServiceStateChanged(object? sender, DragDropState newDragDropState)
    {
        if (_isDisposed)
        {
            return;
        }

        // Never interrupt Media Expanded or ScreenshotHistory.
        if (CurrentState is NotchState.Expanded or NotchState.ScreenshotHistory)
        {
            return;
        }

        if (newDragDropState.IsDragging)
        {
            DragDropStateChanged?.Invoke(this, newDragDropState);
            OnDragEnter();
        }
        else if (newDragDropState.HasDropPreview && !ReferenceEquals(newDragDropState, _lastCommittedDropState))
        {
            _lastCommittedDropState = newDragDropState;
            _isPointerHovered = true;
            _isUserScreenshotSessionActive = false;
            DragDropStateChanged?.Invoke(this, newDragDropState);
            OnFileDropped(newDragDropState);
        }
        else
        {
            if (!newDragDropState.HasDropPreview)
            {
                _lastCommittedDropState = DragDropState.Empty;
            }

            DragDropStateChanged?.Invoke(this, newDragDropState);

            if (CurrentState == NotchState.DropTarget)
            {
                OnDragLeave();
            }
        }
    }

    private void OnCalendarServiceStateChanged(object? sender, CalendarState newCalendarState)
    {
        OnCalendarStateChanged(newCalendarState);
    }

    private void OnGlobalScreenshotHotkeyPressed(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = RequestScreenshotCaptureAsync();
    }

    private void OnSystemTrayShowRequested(object? sender, EventArgs e)
    {
        ShowNotch();
    }

    private void OnSystemTrayHideRequested(object? sender, EventArgs e)
    {
        HideNotch();
    }

    private void OnSystemTrayExitRequested(object? sender, EventArgs e)
    {
        RequestApplicationExit();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CancelPendingCollapse();

        _systemTrayService.ShowRequested -= OnSystemTrayShowRequested;
        _systemTrayService.HideRequested -= OnSystemTrayHideRequested;
        _systemTrayService.ExitRequested -= OnSystemTrayExitRequested;
        _globalHotkeyService.ScreenshotHotkeyPressed -= OnGlobalScreenshotHotkeyPressed;
        _calendarService.CalendarStateChanged -= OnCalendarServiceStateChanged;
        _mediaService.MediaStateChanged -= OnMediaServiceStateChanged;
        _clipboardService.ClipboardStateChanged -= OnClipboardServiceStateChanged;
        _dragDropService.DragDropStateChanged -= OnDragDropServiceStateChanged;
        _screenshotService.ScreenshotStateChanged -= OnScreenshotServiceStateChanged;
        _screenshotService.RecentScreenshotsChanged -= OnScreenshotServiceRecentChanged;
        _clockService.TimeChanged -= OnClockServiceTimeChanged;

        // Orderly service shutdown per Step 15 specification:
        // 1. Stop tray service
        _systemTrayService.Stop();
        _systemTrayService.Dispose();

        // 2. Unregister global hotkey
        _globalHotkeyService.Stop();
        _globalHotkeyService.Dispose();

        // 3. Stop calendar service
        _calendarService.Stop();
        _calendarService.Dispose();

        // 4. Stop media service
        _mediaService.Stop();
        _mediaService.Dispose();

        // 5. Stop clipboard listener
        _clipboardService.Stop();
        _clipboardService.Dispose();

        // 6. Revoke drag/drop registration
        _dragDropService.Stop();
        _dragDropService.Dispose();

        // 7. Dispose screenshot service
        _screenshotService.Dispose();

        // 8. Stop and dispose clock service
        _clockService.Stop();
        _clockService.Dispose();
    }
}

