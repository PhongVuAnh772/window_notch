using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WindowsNotch.Animation;
using WindowsNotch.Core;
using WindowsNotch.Notch;
using WindowsNotch.Platform.Windows;
using WindowsNotch.Services.Calendar;
using WindowsNotch.Services.Clipboard;
using WindowsNotch.Services.DragDrop;
using WindowsNotch.Services.Media;
using WindowsNotch.Services.Screenshot;

namespace WindowsNotch.UI;

/// <summary>
/// Persistent single Notch window surface.
/// Morphs between Idle, Media, Clipboard, Screenshot, ScreenshotHistory, DropTarget, Calendar, Hover, and Expanded states in-place via Windows Composition animations
/// with layered soft shadow, dark glass surface, 1px subtle border, real-time HH:mm clock,
/// event-driven Windows system media integration, transport controls, timeline seek bar,
/// lightweight clipboard preview, in-memory primary-display screenshot preview + recent screenshot history (up to 3 items),
/// native OLE drag-and-drop file preview, and read-only upcoming Windows calendar event preview.
/// </summary>
public sealed partial class NotchWindow : Window
{
    private const string PlayAccessibleName = "Play";
    private const string PauseAccessibleName = "Pause";
    private const string DefaultZeroTimeText = "0:00";

    private readonly NotchController _controller;
    private readonly WindowManager _windowManager;
    private readonly NotchAnimator _animator;
    private readonly Brush _accentBrush;
    private readonly Brush _secondaryBrush;
    private readonly Brush _historyDefaultBorderBrush;
    private readonly Brush _historySelectedBorderBrush;
    private readonly Thickness _mediaHoverPadding;
    private readonly Thickness _mediaExpandedPadding;
    private readonly PointerEventHandler _transportButtonPressedHandler;
    private readonly PointerEventHandler _transportButtonReleasedHandler;
    private readonly PointerEventHandler _historyItemPressedHandler;
    private readonly PointerEventHandler _historyItemReleasedHandler;

    private byte[]? _displayedArtworkBytes;
    private string _displayedTrackTitle = string.Empty;
    private string _displayedTrackArtist = string.Empty;
    private int _artworkLoadVersion;

    private byte[]? _displayedClipboardImageBytes;
    private string _displayedClipboardPreviewText = string.Empty;
    private ClipboardContentType _displayedClipboardContentType = ClipboardContentType.None;
    private int _clipboardImageLoadVersion;

    private byte[]? _displayedScreenshotBytes;
    private int _displayedScreenshotWidth;
    private int _displayedScreenshotHeight;
    private int _screenshotImageLoadVersion;
    private int _screenshotHistoryLoadVersion;

    private CalendarEvent? _displayedCalendarEvent;

    private bool _isMetadataPressPending;
    private bool _isPreviousButtonHovered;
    private bool _isPreviousButtonPressed;
    private bool _isPlayPauseButtonHovered;
    private bool _isPlayPauseButtonPressed;
    private bool _isNextButtonHovered;
    private bool _isNextButtonPressed;
    private bool _isClockScreenshotButtonHovered;
    private bool _isClockScreenshotButtonPressed;
    private bool _isClipboardScreenshotButtonHovered;
    private bool _isClipboardScreenshotButtonPressed;
    private bool _isCalendarScreenshotButtonHovered;
    private bool _isCalendarScreenshotButtonPressed;
    private bool _isScreenshotHistoryButtonHovered;
    private bool _isScreenshotHistoryButtonPressed;
    private bool _isScreenshotRecaptureButtonHovered;
    private bool _isScreenshotRecaptureButtonPressed;

    private readonly bool[] _isHistoryItemHovered = new bool[DesignTokens.Screenshot.MaxHistoryCount];
    private readonly bool[] _isHistoryItemPressed = new bool[DesignTokens.Screenshot.MaxHistoryCount];

    private bool _isSeekSurfaceHovered;
    private bool _isSeeking;
    private TimeSpan _pendingSeekPosition;

    private bool _hasCompletedInitialActivation;
    private bool _isShuttingDown;
    private bool _isClosed;

    public NotchWindow()
        : this(new NotchController())
    {
    }

    public NotchWindow(NotchController controller)
    {
        _controller = controller;

        InitializeComponent();

        _accentBrush = (Brush)Application.Current.Resources["NotchAccentBrush"];
        _secondaryBrush = (Brush)Application.Current.Resources["NotchSecondaryBrush"];
        _historyDefaultBorderBrush = (Brush)Application.Current.Resources["ScreenshotHistoryThumbnailBorderBrush"];
        _historySelectedBorderBrush = (Brush)Application.Current.Resources["ScreenshotHistoryThumbnailSelectedBorderBrush"];
        _mediaHoverPadding = (Thickness)Application.Current.Resources["MediaHoverPadding"];
        _mediaExpandedPadding = (Thickness)Application.Current.Resources["MediaExpandedPadding"];

        _transportButtonPressedHandler = OnTransportButtonPointerPressed;
        _transportButtonReleasedHandler = OnTransportButtonPointerReleased;
        _historyItemPressedHandler = OnScreenshotHistoryItemPointerPressed;
        _historyItemReleasedHandler = OnScreenshotHistoryItemPointerReleased;

        _windowManager = new WindowManager(this);
        _animator = new NotchAnimator(
            new NotchVisualLayers(
                ShadowLayer,
                NotchSurface,
                BackdropGlassLayer,
                SurfaceBaseLayer,
                SurfaceDepthLayer,
                BorderHighlightLayer,
                ContentHost,
                ClockViewHost,
                CollapsedMediaIndicator,
                CollapsedClipboardIndicator,
                CollapsedScreenshotIndicator,
                CollapsedDropIndicator,
                CollapsedCalendarIndicator,
                ClockHoverScreenshotActionHost,
                ClockPrimaryText,
                ClockSecondaryText,
                MediaViewHost,
                MediaControlsHost,
                MediaTimelineRow,
                SeekTrackBackground,
                SeekTrackFill,
                SeekThumb,
                ClipboardHoverViewHost,
                ScreenshotPreviewViewHost,
                ScreenshotHistoryViewHost,
                DropTargetViewHost,
                DropTargetPromptPanel,
                DropPreviewContentPanel,
                CalendarHoverViewHost));

        AttachTransportButtonHandlers(PreviousTrackButton);
        AttachTransportButtonHandlers(PlayPauseButton);
        AttachTransportButtonHandlers(NextTrackButton);
        AttachTransportButtonHandlers(ClockScreenshotButton);
        AttachTransportButtonHandlers(ClipboardScreenshotButton);
        AttachTransportButtonHandlers(CalendarScreenshotButton);
        AttachTransportButtonHandlers(ScreenshotHistoryButton);
        AttachTransportButtonHandlers(ScreenshotRecaptureButton);

        AttachHistoryItemHandlers(ScreenshotHistoryItem0Button);
        AttachHistoryItemHandlers(ScreenshotHistoryItem1Button);
        AttachHistoryItemHandlers(ScreenshotHistoryItem2Button);

        NotchSurface.KeyDown += OnNotchSurfaceKeyDown;

        _windowManager.ClipboardMessageReceived += OnWindowManagerClipboardMessageReceived;
        _windowManager.NativeHotkeyPressed += OnWindowManagerNativeHotkeyPressed;
        _windowManager.NativeTrayMessageReceived += OnWindowManagerNativeTrayMessageReceived;
        _windowManager.WindowCloseRequested += OnWindowManagerWindowCloseRequested;
        _windowManager.CursorOutsideInteractiveBoundsDetected += OnWindowManagerCursorOutsideInteractiveBoundsDetected;
        _controller.StateChanged += OnControllerStateChanged;
        _controller.NotchVisibilityChanged += OnControllerNotchVisibilityChanged;
        _controller.ApplicationExitRequested += OnControllerApplicationExitRequested;
        _controller.TimeTextChanged += OnControllerTimeTextChanged;
        _controller.MediaStateChanged += OnControllerMediaStateChanged;
        _controller.ClipboardStateChanged += OnControllerClipboardStateChanged;
        _controller.ScreenshotStateChanged += OnControllerScreenshotStateChanged;
        _controller.RecentScreenshotsChanged += OnControllerRecentScreenshotsChanged;
        _controller.DragDropStateChanged += OnControllerDragDropStateChanged;
        _controller.CalendarStateChanged += OnControllerCalendarStateChanged;
        Activated += OnWindowActivated;
        Closed += OnWindowClosed;

        InitializeIdleShell();
    }

    public event EventHandler? ApplicationExitRequested;

    public bool IsNotchVisible => !_isClosed && _controller.IsNotchVisible;

    public bool ShowNotch()
    {
        return !_isClosed && _controller.ShowNotch();
    }

    public bool HideNotch()
    {
        return !_isClosed && _controller.HideNotch();
    }

    public void ShutdownAndClose()
    {
        if (_isShuttingDown || _isClosed)
        {
            return;
        }

        _isShuttingDown = true;

        // 1–8. Orderly shutdown of tray, hotkey, calendar, media, clipboard, drag/drop, screenshot, and controller.
        _controller.Dispose();

        // 9. Permit native window close and destroy NotchWindow.
        _windowManager.AllowWindowClose();
        Close();
    }

    private void InitializeIdleShell()
    {
        _controller.StartClipboardMonitoring(_windowManager.WindowHandle);
        _controller.StartDragDropMonitoring(_windowManager.WindowHandle, _windowManager.IsScreenPointInsideInteractivePill);
        _controller.StartGlobalHotkeyMonitoring(_windowManager.WindowHandle);
        _controller.StartSystemTrayMonitoring(_windowManager.WindowHandle);

        NotchDimensions initial = _controller.GetDimensionsForCurrentState();

        ApplyHostCanvasBounds(initial.HostCanvasWidth, initial.HostCanvasHeight);

        _animator.InitializeMediaControlButton(PreviousTrackButton, PreviousButtonBg, PreviousButtonIcon);
        _animator.InitializeMediaControlButton(PlayPauseButton, PlayPauseButtonBg, PlayPauseButtonIconHost);
        _animator.InitializeMediaControlButton(NextTrackButton, NextButtonBg, NextButtonIcon);
        _animator.InitializeMediaControlButton(ClockScreenshotButton, ClockScreenshotButtonBg, ClockScreenshotButtonIcon);
        _animator.InitializeMediaControlButton(ClipboardScreenshotButton, ClipboardScreenshotButtonBg, ClipboardScreenshotButtonIcon);
        _animator.InitializeMediaControlButton(CalendarScreenshotButton, CalendarScreenshotButtonBg, CalendarScreenshotButtonIcon);
        _animator.InitializeMediaControlButton(ScreenshotHistoryButton, ScreenshotHistoryButtonBg, ScreenshotHistoryButtonIcon);
        _animator.InitializeMediaControlButton(ScreenshotRecaptureButton, ScreenshotRecaptureButtonBg, ScreenshotRecaptureButtonIcon);

        _animator.InitializeScreenshotHistoryItem(ScreenshotHistoryItem0Button);
        _animator.InitializeScreenshotHistoryItem(ScreenshotHistoryItem1Button);
        _animator.InitializeScreenshotHistoryItem(ScreenshotHistoryItem2Button);

        ApplyMediaLayoutForState(NotchState.Hover);
        ApplyMediaMetadataToView(_controller.CurrentMediaState, animateTrackChange: false);
        ApplyClipboardStateToView(_controller.CurrentClipboardState, animateChange: false);
        ApplyScreenshotStateToView(_controller.CurrentScreenshotState, animateChange: false);
        UpdateScreenshotHistoryAffordanceVisibility();
        ApplyDragDropStateToView(_controller.CurrentDragDropState, animateChange: false);
        ApplyCalendarStateToView(_controller.CurrentCalendarState, animateChange: false);
        UpdateCollapsedClockOrCalendarVisibility(
            useCalendarTimeInClockHost: !_controller.HasActiveMedia &&
                                        !_controller.HasActiveClipboard &&
                                        !_controller.HasActiveScreenshot &&
                                        !_controller.HasActiveDropPreview &&
                                        _controller.HasActiveCalendarEvent);
        _animator.InitializeRestState(
            initial,
            _controller.CurrentTimeText,
            _controller.HasActiveMedia,
            _controller.HasActiveClipboard,
            _controller.HasActiveScreenshot,
            _controller.HasActiveDropPreview,
            _controller.HasActiveCalendarEvent);
        UpdateMediaInteractivity(activeState: _controller.CurrentState, hasMedia: _controller.HasActiveMedia);

        _windowManager.InitializeNotchShell(
            initial.HostCanvasWidth,
            initial.HostCanvasHeight,
            initial.LogicalWidth,
            initial.LogicalHeight);
    }

    private void AttachTransportButtonHandlers(Button button)
    {
        button.AddHandler(UIElement.PointerPressedEvent, _transportButtonPressedHandler, handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, _transportButtonReleasedHandler, handledEventsToo: true);
    }

    private void DetachTransportButtonHandlers(Button button)
    {
        button.RemoveHandler(UIElement.PointerPressedEvent, _transportButtonPressedHandler);
        button.RemoveHandler(UIElement.PointerReleasedEvent, _transportButtonReleasedHandler);
    }

    private void AttachHistoryItemHandlers(Button button)
    {
        button.AddHandler(UIElement.PointerPressedEvent, _historyItemPressedHandler, handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, _historyItemReleasedHandler, handledEventsToo: true);
    }

    private void DetachHistoryItemHandlers(Button button)
    {
        button.RemoveHandler(UIElement.PointerPressedEvent, _historyItemPressedHandler);
        button.RemoveHandler(UIElement.PointerReleasedEvent, _historyItemReleasedHandler);
    }

    private void OnNotchSurfaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_controller.IsNotchVisible)
        {
            return;
        }

        if (e.Key == VirtualKey.Escape && _controller.CurrentState == NotchState.ScreenshotHistory)
        {
            e.Handled = true;
            _controller.DismissScreenshotHistory(IsCursorStillInsideHoverNotchPill);
        }
    }

    private void OnNotchPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_controller.IsNotchVisible)
        {
            return;
        }

        if (!IsPointerPointInsideActivePill(e))
        {
            return;
        }

        _controller.OnPointerEntered();
    }

    private void OnNotchPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_controller.IsNotchVisible)
        {
            return;
        }

        if (!IsPointerPointInsideActivePill(e))
        {
            if (!_isSeeking && !NotchController.IsCollapsedState(_controller.CurrentState))
            {
                _controller.OnPointerExited(IsCursorStillInsideActiveNotchPill);
            }

            return;
        }

        _controller.OnPointerMovedWithinNotch();
    }

    private void OnNotchPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_controller.IsNotchVisible)
        {
            return;
        }

        // Do not collapse while the user is actively dragging the seek bar with pointer capture.
        if (_isSeeking)
        {
            return;
        }

        _controller.OnPointerExited(IsCursorStillInsideActiveNotchPill);
    }

    private bool IsPointerPointInsideActivePill(PointerRoutedEventArgs e)
    {
        NotchDimensions active = _controller.GetDimensionsForCurrentState();
        Point pt = e.GetCurrentPoint(NotchSurface).Position;
        double pillLeft = (DesignTokens.Surface.HostCanvasWidth - active.LogicalWidth) * 0.5;
        double pillRight = pillLeft + active.LogicalWidth;
        return pt.X >= pillLeft && pt.X <= pillRight && pt.Y >= 0.0 && pt.Y <= active.LogicalHeight;
    }

    private void OnNotchDragEnter(object sender, DragEventArgs e)
    {
        HandleNotchDragEnterOrOver(e);
    }

    private void OnNotchDragOver(object sender, DragEventArgs e)
    {
        HandleNotchDragEnterOrOver(e);
    }

    private void HandleNotchDragEnterOrOver(DragEventArgs e)
    {
        if (!_controller.CanAcceptDrag || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
        }

        _controller.NotifyExternalDragEnter();
    }

    private void OnNotchDragLeave(object sender, DragEventArgs e)
    {
        _controller.NotifyExternalDragLeave();
    }

    private async void OnNotchDrop(object sender, DragEventArgs e)
    {
        if (!_controller.CanAcceptDrag || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            _controller.NotifyExternalDragLeave();
            return;
        }

        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            string? firstPath = null;
            foreach (IStorageItem item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Path))
                {
                    firstPath = item.Path;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(firstPath))
            {
                _controller.TryAcceptExternalFileDrop(firstPath);
            }
            else
            {
                _controller.NotifyExternalDragLeave();
            }
        }
        catch
        {
            _controller.NotifyExternalDragLeave();
        }
    }

    private void OnWindowManagerCursorOutsideInteractiveBoundsDetected(object? sender, EventArgs e)
    {
        if (_isClosed || !_controller.IsNotchVisible || _isSeeking || _controller.HasActiveDrag || NotchController.IsCollapsedState(_controller.CurrentState))
        {
            return;
        }

        _controller.OnPointerExited(IsCursorStillInsideActiveNotchPill);
    }

    private bool IsCursorStillInsideActiveNotchPill()
    {
        NotchDimensions activeDimensions = _controller.GetDimensionsForCurrentState();
        return _windowManager.IsCursorInsideLogicalBounds(
            activeDimensions.LogicalWidth,
            activeDimensions.LogicalHeight);
    }

    private bool IsCursorStillInsideHoverNotchPill()
    {
        NotchDimensions hoverDimensions = NotchController.GetDimensions(NotchState.Hover);
        return _windowManager.IsCursorInsideLogicalBounds(
            hoverDimensions.LogicalWidth,
            hoverDimensions.LogicalHeight);
    }

    private void OnMediaMetadataPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        if (_controller.CurrentState == NotchState.Hover && _controller.HasActiveMedia)
        {
            _isMetadataPressPending = true;
        }
    }

    private void OnMediaMetadataPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        if (!_isMetadataPressPending)
        {
            return;
        }

        _isMetadataPressPending = false;
        _controller.RequestExpandMedia();
    }

    private void OnMediaMetadataPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _isMetadataPressPending = false;
    }

    private void OnMediaMetadataPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _isMetadataPressPending = false;
    }

    private async void OnPreviousTrackButtonClick(object sender, RoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        await _controller.SkipPreviousMediaAsync();
    }

    private async void OnPlayPauseButtonClick(object sender, RoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        await _controller.PlayPauseMediaAsync();
    }

    private async void OnNextTrackButtonClick(object sender, RoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        await _controller.SkipNextMediaAsync();
    }

    private async void OnScreenshotCaptureButtonClick(object sender, RoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        await _controller.RequestScreenshotCapture();
    }

    private void OnScreenshotHistoryButtonClick(object sender, RoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        _controller.RequestOpenScreenshotHistory();
    }

    private void OnScreenshotHistoryItemClick(object sender, RoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        if (TryGetHistoryItemIndex(sender, out int index))
        {
            _controller.SelectRecentScreenshot(index);
        }
    }

    private void OnScreenshotHistoryItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        SetHistoryItemVisualState(sender, isHovered: true, isPressed: null);
    }

    private void OnScreenshotHistoryItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetHistoryItemVisualState(sender, isHovered: false, isPressed: false);
    }

    private void OnScreenshotHistoryItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        SetHistoryItemVisualState(sender, isHovered: true, isPressed: true);
    }

    private void OnScreenshotHistoryItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        SetHistoryItemVisualState(sender, isHovered: null, isPressed: false);
    }

    private void OnScreenshotHistoryItemPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        SetHistoryItemVisualState(sender, isHovered: false, isPressed: false);
    }

    private void OnScreenshotHistoryItemPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        SetHistoryItemVisualState(sender, isHovered: null, isPressed: false);
    }

    private static bool TryGetHistoryItemIndex(object sender, out int index)
    {
        if (sender is FrameworkElement { Tag: string tagText } &&
            int.TryParse(tagText, out int parsed) &&
            parsed >= 0 &&
            parsed < DesignTokens.Screenshot.MaxHistoryCount)
        {
            index = parsed;
            return true;
        }

        index = -1;
        return false;
    }

    private void SetHistoryItemVisualState(object sender, bool? isHovered, bool? isPressed)
    {
        if (_isClosed || !TryGetHistoryItemIndex(sender, out int index) || sender is not FrameworkElement itemElement)
        {
            return;
        }

        if (isHovered.HasValue)
        {
            _isHistoryItemHovered[index] = isHovered.Value;
        }

        if (isPressed.HasValue)
        {
            _isHistoryItemPressed[index] = isPressed.Value;
        }

        UpdateHistoryItemBorderBrush(index);
        _animator.AnimateScreenshotHistoryItemInteraction(
            itemElement,
            _isHistoryItemHovered[index],
            _isHistoryItemPressed[index]);
    }

    private void UpdateHistoryItemBorderBrush(int index)
    {
        Border cardBorder = index switch
        {
            0 => ScreenshotHistoryItem0CardBorder,
            1 => ScreenshotHistoryItem1CardBorder,
            _ => ScreenshotHistoryItem2CardBorder
        };

        IReadOnlyList<RecentScreenshot> recent = _controller.RecentScreenshots;
        bool isSelected = index >= 0 &&
                          index < recent.Count &&
                          ReferenceEquals(recent[index].ImageData, _controller.CurrentScreenshotState.ImageData);

        bool showHighlight = isSelected || _isHistoryItemHovered[index] || _isHistoryItemPressed[index];
        cardBorder.BorderBrush = showHighlight ? _historySelectedBorderBrush : _historyDefaultBorderBrush;
    }

    private void OnTransportButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        SetTransportButtonVisualState(sender, isHovered: true, isPressed: null);
    }

    private void OnTransportButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetTransportButtonVisualState(sender, isHovered: false, isPressed: false);
    }

    private void OnTransportButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        SetTransportButtonVisualState(sender, isHovered: true, isPressed: true);
    }

    private void OnTransportButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        SetTransportButtonVisualState(sender, isHovered: null, isPressed: false);
    }

    private void OnTransportButtonPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        SetTransportButtonVisualState(sender, isHovered: false, isPressed: false);
    }

    private void OnTransportButtonPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        SetTransportButtonVisualState(sender, isHovered: null, isPressed: false);
    }

    private void OnSeekSurfacePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();
        _isSeekSurfaceHovered = true;
        _animator.AnimateSeekThumbState(isActive: true);
    }

    private void OnSeekSurfacePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isSeekSurfaceHovered = false;
        if (!_isSeeking)
        {
            _animator.AnimateSeekThumbState(isActive: false);
        }
    }

    private void OnSeekSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();

        MediaTimeline? timeline = _controller.CurrentMediaState.Timeline;
        if (_controller.CurrentState != NotchState.Expanded || timeline is null || !timeline.CanSeek)
        {
            return;
        }

        _isSeeking = true;
        SeekInteractiveSurface.CapturePointer(e.Pointer);
        _animator.AnimateSeekThumbState(isActive: true);

        UpdateLocalSeekPreviewFromPointer(e, timeline);
        e.Handled = true;
    }

    private void OnSeekSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();

        if (!_isSeeking)
        {
            return;
        }

        MediaTimeline? timeline = _controller.CurrentMediaState.Timeline;
        if (timeline is null || !timeline.CanSeek)
        {
            CancelActiveSeekPreview();
            return;
        }

        // Only update the local visual preview during drag; never call TryChangePlaybackPositionAsync here.
        UpdateLocalSeekPreviewFromPointer(e, timeline);
        e.Handled = true;
    }

    private async void OnSeekSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _controller.OnPointerMovedWithinNotch();

        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        SeekInteractiveSurface.ReleasePointerCapture(e.Pointer);
        _animator.AnimateSeekThumbState(isActive: _isSeekSurfaceHovered);
        e.Handled = true;

        TimeSpan requestedPosition = _pendingSeekPosition;
        bool succeeded = await _controller.SeekMediaAsync(requestedPosition);

        if (_isClosed)
        {
            return;
        }

        if (!succeeded)
        {
            // Restore the authoritative timeline position if the seek request failed.
            SyncTimelineToView(_controller.CurrentMediaState);
        }

        if (!IsCursorStillInsideActiveNotchPill())
        {
            _controller.OnPointerExited(IsCursorStillInsideActiveNotchPill);
        }
    }

    private void OnSeekSurfacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelActiveSeekPreview();
    }

    private void OnSeekSurfacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isSeeking)
        {
            CancelActiveSeekPreview();
        }
    }

    private void UpdateLocalSeekPreviewFromPointer(PointerRoutedEventArgs e, MediaTimeline timeline)
    {
        double trackWidth = SeekInteractiveSurface.ActualWidth;
        if (trackWidth <= 0.0 || !timeline.HasValidTimeline)
        {
            return;
        }

        Point pointerPoint = e.GetCurrentPoint(SeekInteractiveSurface).Position;
        double normalizedRatio = Math.Clamp(pointerPoint.X / trackWidth, 0.0, 1.0);

        TimeSpan clampedSeekPosition = timeline.ResolveSeekPosition(normalizedRatio);
        _pendingSeekPosition = clampedSeekPosition;

        TimeSpan previewElapsed = clampedSeekPosition - timeline.StartTime;
        if (previewElapsed < TimeSpan.Zero)
        {
            previewElapsed = TimeSpan.Zero;
        }
        else if (previewElapsed > timeline.Duration)
        {
            previewElapsed = timeline.Duration;
        }

        double previewRatio = timeline.Duration.TotalSeconds > 0.0
            ? Math.Clamp(previewElapsed.TotalSeconds / timeline.Duration.TotalSeconds, 0.0, 1.0)
            : 0.0;

        MediaElapsedText.Text = FormatMediaTime(previewElapsed);
        _animator.SetSeekPreviewProgress(previewRatio);
    }

    private void CancelActiveSeekPreview()
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        _animator.AnimateSeekThumbState(isActive: _isSeekSurfaceHovered);
        SyncTimelineToView(_controller.CurrentMediaState);
    }

    private void SetTransportButtonVisualState(object sender, bool? isHovered, bool? isPressed)
    {
        if (_isClosed)
        {
            return;
        }

        if (ReferenceEquals(sender, PreviousTrackButton))
        {
            if (isHovered.HasValue)
            {
                _isPreviousButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isPreviousButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                PreviousTrackButton,
                PreviousButtonBg,
                PreviousButtonIcon,
                _isPreviousButtonHovered,
                _isPreviousButtonPressed);
        }
        else if (ReferenceEquals(sender, PlayPauseButton))
        {
            if (isHovered.HasValue)
            {
                _isPlayPauseButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isPlayPauseButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                PlayPauseButton,
                PlayPauseButtonBg,
                PlayPauseButtonIconHost,
                _isPlayPauseButtonHovered,
                _isPlayPauseButtonPressed);
        }
        else if (ReferenceEquals(sender, NextTrackButton))
        {
            if (isHovered.HasValue)
            {
                _isNextButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isNextButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                NextTrackButton,
                NextButtonBg,
                NextButtonIcon,
                _isNextButtonHovered,
                _isNextButtonPressed);
        }
        else if (ReferenceEquals(sender, ClockScreenshotButton))
        {
            if (isHovered.HasValue)
            {
                _isClockScreenshotButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isClockScreenshotButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                ClockScreenshotButton,
                ClockScreenshotButtonBg,
                ClockScreenshotButtonIcon,
                _isClockScreenshotButtonHovered,
                _isClockScreenshotButtonPressed);
        }
        else if (ReferenceEquals(sender, ClipboardScreenshotButton))
        {
            if (isHovered.HasValue)
            {
                _isClipboardScreenshotButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isClipboardScreenshotButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                ClipboardScreenshotButton,
                ClipboardScreenshotButtonBg,
                ClipboardScreenshotButtonIcon,
                _isClipboardScreenshotButtonHovered,
                _isClipboardScreenshotButtonPressed);
        }
        else if (ReferenceEquals(sender, CalendarScreenshotButton))
        {
            if (isHovered.HasValue)
            {
                _isCalendarScreenshotButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isCalendarScreenshotButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                CalendarScreenshotButton,
                CalendarScreenshotButtonBg,
                CalendarScreenshotButtonIcon,
                _isCalendarScreenshotButtonHovered,
                _isCalendarScreenshotButtonPressed);
        }
        else if (ReferenceEquals(sender, ScreenshotHistoryButton))
        {
            if (isHovered.HasValue)
            {
                _isScreenshotHistoryButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isScreenshotHistoryButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                ScreenshotHistoryButton,
                ScreenshotHistoryButtonBg,
                ScreenshotHistoryButtonIcon,
                _isScreenshotHistoryButtonHovered,
                _isScreenshotHistoryButtonPressed);
        }
        else if (ReferenceEquals(sender, ScreenshotRecaptureButton))
        {
            if (isHovered.HasValue)
            {
                _isScreenshotRecaptureButtonHovered = isHovered.Value;
            }

            if (isPressed.HasValue)
            {
                _isScreenshotRecaptureButtonPressed = isPressed.Value;
            }

            _animator.AnimateMediaControlButtonState(
                ScreenshotRecaptureButton,
                ScreenshotRecaptureButtonBg,
                ScreenshotRecaptureButtonIcon,
                _isScreenshotRecaptureButtonHovered,
                _isScreenshotRecaptureButtonPressed);
        }
    }

    private void ApplyMediaLayoutForState(NotchState state)
    {
        bool isExpanded = state == NotchState.Expanded;

        MediaViewHost.Width = isExpanded
            ? DesignTokens.NotchSize.MediaExpandedWidth
            : DesignTokens.NotchSize.HoverWidth;
        MediaViewHost.Height = isExpanded
            ? DesignTokens.NotchSize.MediaExpandedHeight
            : DesignTokens.NotchSize.HoverHeight;
        MediaViewHost.Padding = isExpanded ? _mediaExpandedPadding : _mediaHoverPadding;

        double artworkSize = isExpanded
            ? DesignTokens.Media.ExpandedArtworkSize
            : DesignTokens.Media.HoverArtworkSize;
        MediaArtworkHost.Width = artworkSize;
        MediaArtworkHost.Height = artworkSize;

        MediaTitleText.FontSize = isExpanded
            ? DesignTokens.Typography.BodySize
            : DesignTokens.Typography.SecondarySize;
        MediaArtistText.FontSize = isExpanded
            ? DesignTokens.Typography.SecondarySize
            : DesignTokens.Typography.CaptionSize;

        MediaTimelineRow.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMediaInteractivity(NotchState activeState, bool hasMedia)
    {
        bool isMediaInteractive = hasMedia && activeState is NotchState.Hover or NotchState.Expanded;
        bool isHoverExpandable = hasMedia && activeState == NotchState.Hover;
        bool isExpanded = hasMedia && activeState == NotchState.Expanded;
        bool canSeek = isExpanded && (_controller.CurrentMediaState.Timeline?.CanSeek ?? false);

        bool isHover = activeState == NotchState.Hover;
        bool isScreenshotHistory = activeState == NotchState.ScreenshotHistory;
        bool hasClipboard = _controller.HasActiveClipboard;
        bool hasScreenshot = _controller.HasActiveScreenshot;
        bool hasRecentScreenshots = _controller.HasRecentScreenshots;
        bool hasCalendarEvent = _controller.HasActiveCalendarEvent;
        bool showScreenshotInHover = !hasMedia &&
                                     hasScreenshot &&
                                     isHover &&
                                     (!hasClipboard || _controller.ShouldShowScreenshotInHover);
        bool showClipboardInHover = !hasMedia && !showScreenshotInHover && hasClipboard && isHover;
        bool showCalendarInHover = !hasMedia && !showScreenshotInHover && !showClipboardInHover && hasCalendarEvent && isHover;
        bool showClockActionInHover = !hasMedia && !showScreenshotInHover && !showClipboardInHover && !showCalendarInHover && isHover;

        MediaMetadataClickTarget.IsHitTestVisible = isHoverExpandable;
        MediaControlsHost.IsHitTestVisible = isMediaInteractive;
        PreviousTrackButton.IsEnabled = isMediaInteractive;
        PlayPauseButton.IsEnabled = isMediaInteractive;
        NextTrackButton.IsEnabled = isMediaInteractive;

        MediaTimelineRow.IsHitTestVisible = isExpanded;
        SeekInteractiveSurface.IsHitTestVisible = canSeek;

        ClockHoverScreenshotActionHost.IsHitTestVisible = showClockActionInHover;
        ClockScreenshotButton.IsEnabled = showClockActionInHover;

        ClipboardHoverViewHost.IsHitTestVisible = showClipboardInHover;
        ClipboardScreenshotButton.IsEnabled = showClipboardInHover;

        CalendarHoverViewHost.IsHitTestVisible = showCalendarInHover;
        CalendarScreenshotButton.IsEnabled = showCalendarInHover;

        ScreenshotPreviewViewHost.IsHitTestVisible = showScreenshotInHover;
        ScreenshotHistoryButton.IsEnabled = showScreenshotInHover && hasRecentScreenshots;
        ScreenshotRecaptureButton.IsEnabled = showScreenshotInHover;

        ScreenshotHistoryViewHost.IsHitTestVisible = isScreenshotHistory;
        ScreenshotHistoryItem0Button.IsEnabled = isScreenshotHistory;
        ScreenshotHistoryItem1Button.IsEnabled = isScreenshotHistory;
        ScreenshotHistoryItem2Button.IsEnabled = isScreenshotHistory;

        if (!isHoverExpandable)
        {
            _isMetadataPressPending = false;
        }

        if (!isExpanded && _isSeeking)
        {
            _isSeeking = false;
            _isSeekSurfaceHovered = false;
            _animator.AnimateSeekThumbState(isActive: false);
        }

        if (!isMediaInteractive)
        {
            _isPreviousButtonHovered = false;
            _isPreviousButtonPressed = false;
            _isPlayPauseButtonHovered = false;
            _isPlayPauseButtonPressed = false;
            _isNextButtonHovered = false;
            _isNextButtonPressed = false;

            _animator.InitializeMediaControlButton(PreviousTrackButton, PreviousButtonBg, PreviousButtonIcon);
            _animator.InitializeMediaControlButton(PlayPauseButton, PlayPauseButtonBg, PlayPauseButtonIconHost);
            _animator.InitializeMediaControlButton(NextTrackButton, NextButtonBg, NextButtonIcon);
        }

        if (!showClockActionInHover)
        {
            _isClockScreenshotButtonHovered = false;
            _isClockScreenshotButtonPressed = false;
            _animator.InitializeMediaControlButton(ClockScreenshotButton, ClockScreenshotButtonBg, ClockScreenshotButtonIcon);
        }

        if (!showClipboardInHover)
        {
            _isClipboardScreenshotButtonHovered = false;
            _isClipboardScreenshotButtonPressed = false;
            _animator.InitializeMediaControlButton(ClipboardScreenshotButton, ClipboardScreenshotButtonBg, ClipboardScreenshotButtonIcon);
        }

        if (!showCalendarInHover)
        {
            _isCalendarScreenshotButtonHovered = false;
            _isCalendarScreenshotButtonPressed = false;
            _animator.InitializeMediaControlButton(CalendarScreenshotButton, CalendarScreenshotButtonBg, CalendarScreenshotButtonIcon);
        }

        if (!showScreenshotInHover)
        {
            _isScreenshotHistoryButtonHovered = false;
            _isScreenshotHistoryButtonPressed = false;
            _isScreenshotRecaptureButtonHovered = false;
            _isScreenshotRecaptureButtonPressed = false;
            _animator.InitializeMediaControlButton(ScreenshotHistoryButton, ScreenshotHistoryButtonBg, ScreenshotHistoryButtonIcon);
            _animator.InitializeMediaControlButton(ScreenshotRecaptureButton, ScreenshotRecaptureButtonBg, ScreenshotRecaptureButtonIcon);
        }

        if (!isScreenshotHistory)
        {
            for (int i = 0; i < DesignTokens.Screenshot.MaxHistoryCount; i++)
            {
                _isHistoryItemHovered[i] = false;
                _isHistoryItemPressed[i] = false;
            }

            _animator.InitializeScreenshotHistoryItem(ScreenshotHistoryItem0Button);
            _animator.InitializeScreenshotHistoryItem(ScreenshotHistoryItem1Button);
            _animator.InitializeScreenshotHistoryItem(ScreenshotHistoryItem2Button);
        }
    }

    private void OnWindowManagerClipboardMessageReceived(object? sender, EventArgs e)
    {
        _controller.OnClipboardMessageReceived();
    }

    private void OnWindowManagerNativeHotkeyPressed(object? sender, int hotkeyId)
    {
        _controller.OnNativeHotkeyPressed(hotkeyId);
    }

    private void OnWindowManagerNativeTrayMessageReceived(object? sender, NativeTrayMessageEventArgs e)
    {
        _controller.OnNativeTrayMessageReceived(e.WParam, e.LParam);
    }

    private void OnWindowManagerWindowCloseRequested(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            _controller.HideNotch();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => _controller.HideNotch());
        }
    }

    private void OnControllerNotchVisibilityChanged(object? sender, bool isVisible)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleNotchVisibilityChangedOnUiThread(isVisible);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleNotchVisibilityChangedOnUiThread(isVisible));
        }
    }

    private void OnControllerApplicationExitRequested(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleApplicationExitRequestedOnUiThread();
        }
        else
        {
            DispatcherQueue.TryEnqueue(HandleApplicationExitRequestedOnUiThread);
        }
    }

    private void HandleNotchVisibilityChangedOnUiThread(bool isVisible)
    {
        if (_isClosed)
        {
            return;
        }

        if (!isVisible)
        {
            _isSeeking = false;
            _animator.StopSeekTimelineInterpolation();
            ReleaseDecodedScreenshotHistoryBitmaps();
            _windowManager.HideNotchWindow();
            return;
        }

        NotchDimensions dimensions = _controller.GetDimensionsForCurrentState();
        ApplyHostCanvasBounds(dimensions.HostCanvasWidth, dimensions.HostCanvasHeight);
        _windowManager.SetInteractivePillBounds(dimensions.LogicalWidth, dimensions.LogicalHeight);

        ApplyMediaLayoutForState(NotchState.Hover);
        ApplyMediaMetadataToView(_controller.CurrentMediaState, animateTrackChange: false);
        ApplyClipboardStateToView(_controller.CurrentClipboardState, animateChange: false);
        ApplyScreenshotStateToView(_controller.CurrentScreenshotState, animateChange: false);
        UpdateScreenshotHistoryAffordanceVisibility();
        ApplyDragDropStateToView(_controller.CurrentDragDropState, animateChange: false);
        ApplyCalendarStateToView(_controller.CurrentCalendarState, animateChange: false);
        UpdateCollapsedClockOrCalendarVisibility(
            useCalendarTimeInClockHost: !_controller.HasActiveMedia &&
                                        !_controller.HasActiveClipboard &&
                                        !_controller.HasActiveScreenshot &&
                                        !_controller.HasActiveDropPreview &&
                                        _controller.HasActiveCalendarEvent);

        _animator.InitializeRestState(
            dimensions,
            _controller.CurrentTimeText,
            _controller.HasActiveMedia,
            _controller.HasActiveClipboard,
            _controller.HasActiveScreenshot,
            _controller.HasActiveDropPreview,
            _controller.HasActiveCalendarEvent);

        UpdateMediaInteractivity(activeState: _controller.CurrentState, hasMedia: _controller.HasActiveMedia);
        _windowManager.ShowNotchWindow(dimensions.HostCanvasWidth, dimensions.HostCanvasHeight);
        _ = _controller.RequestCalendarRefresh();
    }

    private void HandleApplicationExitRequestedOnUiThread()
    {
        if (_isClosed || _isShuttingDown)
        {
            return;
        }

        if (ApplicationExitRequested is not null)
        {
            ApplicationExitRequested.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ShutdownAndClose();
        }
    }

    private void OnControllerTimeTextChanged(object? sender, string newTimeText)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            if (_isClosed || !_controller.IsNotchVisible)
            {
                return;
            }

            _animator.AnimateClockMinuteTransition(newTimeText);
            RefreshCalendarTimeFormattingOnUiThread();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isClosed || !_controller.IsNotchVisible)
                {
                    return;
                }

                _animator.AnimateClockMinuteTransition(newTimeText);
                RefreshCalendarTimeFormattingOnUiThread();
            });
        }
    }

    private void OnControllerMediaStateChanged(object? sender, MediaState mediaState)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleMediaStateChangedOnUiThread(mediaState);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleMediaStateChangedOnUiThread(mediaState));
        }
    }

    private void OnControllerClipboardStateChanged(object? sender, ClipboardState clipboardState)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleClipboardStateChangedOnUiThread(clipboardState);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleClipboardStateChangedOnUiThread(clipboardState));
        }
    }

    private void OnControllerScreenshotStateChanged(object? sender, ScreenshotState screenshotState)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleScreenshotStateChangedOnUiThread(screenshotState);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleScreenshotStateChangedOnUiThread(screenshotState));
        }
    }

    private void OnControllerRecentScreenshotsChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleRecentScreenshotsChangedOnUiThread();
        }
        else
        {
            DispatcherQueue.TryEnqueue(HandleRecentScreenshotsChangedOnUiThread);
        }
    }

    private void OnControllerDragDropStateChanged(object? sender, DragDropState dragDropState)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleDragDropStateChangedOnUiThread(dragDropState);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleDragDropStateChangedOnUiThread(dragDropState));
        }
    }

    private void OnControllerCalendarStateChanged(object? sender, CalendarState calendarState)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleCalendarStateChangedOnUiThread(calendarState);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleCalendarStateChangedOnUiThread(calendarState));
        }
    }

    private void HandleMediaStateChangedOnUiThread(MediaState mediaState)
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        ApplyMediaMetadataToView(mediaState, animateTrackChange: true);

        NotchState activeState = _controller.CurrentState;
        bool hasMedia = _controller.HasActiveMedia;

        UpdateMediaInteractivity(activeState, hasMedia);
        SyncActiveContentMode();
    }

    private void HandleClipboardStateChangedOnUiThread(ClipboardState clipboardState)
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        ApplyClipboardStateToView(clipboardState, animateChange: true);
        UpdateMediaInteractivity(_controller.CurrentState, _controller.HasActiveMedia);
        SyncActiveContentMode();
    }

    private void HandleScreenshotStateChangedOnUiThread(ScreenshotState screenshotState)
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        ApplyScreenshotStateToView(screenshotState, animateChange: true);
        UpdateScreenshotHistoryAffordanceVisibility();
        UpdateMediaInteractivity(_controller.CurrentState, _controller.HasActiveMedia);
        SyncActiveContentMode();
    }

    private void HandleRecentScreenshotsChangedOnUiThread()
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        UpdateScreenshotHistoryAffordanceVisibility();
        if (_controller.CurrentState == NotchState.ScreenshotHistory)
        {
            _ = PopulateAndDecodeScreenshotHistoryAsync(animateThumbnailsAppear: false);
        }

        UpdateMediaInteractivity(_controller.CurrentState, _controller.HasActiveMedia);
    }

    private void HandleDragDropStateChangedOnUiThread(DragDropState dragDropState)
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        ApplyDragDropStateToView(dragDropState, animateChange: true);
        UpdateMediaInteractivity(_controller.CurrentState, _controller.HasActiveMedia);
        SyncActiveContentMode();
    }

    private void HandleCalendarStateChangedOnUiThread(CalendarState calendarState)
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        ApplyCalendarStateToView(calendarState, animateChange: true);
        UpdateMediaInteractivity(_controller.CurrentState, _controller.HasActiveMedia);
        SyncActiveContentMode();
    }

    private void ApplyCalendarStateToView(CalendarState calendarState, bool animateChange)
    {
        if (calendarState.HasUpcomingEvent && calendarState.NextEvent is not null)
        {
            bool hadPreviousEvent = _displayedCalendarEvent is not null;
            bool eventChanged = _displayedCalendarEvent != calendarState.NextEvent;
            _displayedCalendarEvent = calendarState.NextEvent;

            CalendarEventTitleText.Text = calendarState.NextEvent.Title;
            RefreshCalendarTimeFormattingOnUiThread();

            if (animateChange && hadPreviousEvent && eventChanged)
            {
                bool hasHigherPriority = _controller.HasActiveMedia ||
                                         _controller.HasActiveClipboard ||
                                         _controller.HasActiveScreenshot ||
                                         _controller.HasActiveDropPreview;
                if (!hasHigherPriority && _controller.CurrentState == NotchState.Hover)
                {
                    _animator.AnimateActiveCalendarChange();
                }
            }
        }
        else
        {
            _displayedCalendarEvent = null;
            CollapsedCalendarTimeText.Text = string.Empty;
            CalendarEventTitleText.Text = string.Empty;
            CalendarEventSubtitleText.Text = string.Empty;
        }
    }

    private void RefreshCalendarTimeFormattingOnUiThread()
    {
        if (_displayedCalendarEvent is null)
        {
            return;
        }

        DateTimeOffset nowLocal = DateTimeOffset.Now;
        CollapsedCalendarTimeText.Text = _displayedCalendarEvent.FormatTimeDisplay(nowLocal);
        CalendarEventSubtitleText.Text = _displayedCalendarEvent.FormatHoverSubtitle(nowLocal);
    }

    private void UpdateCollapsedClockOrCalendarVisibility(bool useCalendarTimeInClockHost)
    {
        ClockTextHost.Visibility = useCalendarTimeInClockHost ? Visibility.Collapsed : Visibility.Visible;
        CollapsedCalendarTimeText.Visibility = useCalendarTimeInClockHost ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncActiveContentMode()
    {
        NotchState activeState = _controller.CurrentState;
        bool hasMedia = _controller.HasActiveMedia;
        bool hasClipboard = _controller.HasActiveClipboard;
        bool hasScreenshot = _controller.HasActiveScreenshot;
        bool shouldShowScreenshotInHover = _controller.ShouldShowScreenshotInHover;
        bool hasActiveDrag = _controller.HasActiveDrag;
        bool hasActiveDropPreview = _controller.HasActiveDropPreview;
        bool hasCalendarEvent = _controller.HasActiveCalendarEvent;

        bool showDropTargetView = activeState == NotchState.DropTarget;
        bool showDropPromptMode = hasActiveDrag || !hasActiveDropPreview;
        bool showScreenshotHistoryView = !showDropTargetView && activeState == NotchState.ScreenshotHistory;
        bool showMediaView = !showDropTargetView &&
                             !showScreenshotHistoryView &&
                             hasMedia &&
                             activeState is NotchState.Hover or NotchState.Expanded;
        bool showScreenshotView = !showDropTargetView &&
                                  !showScreenshotHistoryView &&
                                  !hasMedia &&
                                  hasScreenshot &&
                                  activeState == NotchState.Hover &&
                                  (!hasClipboard || shouldShowScreenshotInHover);
        bool showClipboardView = !showDropTargetView &&
                                 !showScreenshotHistoryView &&
                                 !hasMedia &&
                                 !showScreenshotView &&
                                 hasClipboard &&
                                 activeState == NotchState.Hover;
        bool showCalendarView = !showDropTargetView &&
                                !showScreenshotHistoryView &&
                                !hasMedia &&
                                !showScreenshotView &&
                                !showClipboardView &&
                                hasCalendarEvent &&
                                activeState == NotchState.Hover;
        bool showClockHoverScreenshotAction = !showDropTargetView &&
                                              !showScreenshotHistoryView &&
                                              !hasMedia &&
                                              !showClipboardView &&
                                              !showScreenshotView &&
                                              !showCalendarView &&
                                              activeState == NotchState.Hover;
        bool showExpandedTimeline = hasMedia && activeState == NotchState.Expanded;
        bool isCollapsed = !showMediaView &&
                           !showClipboardView &&
                           !showScreenshotView &&
                           !showScreenshotHistoryView &&
                           !showCalendarView &&
                           !showDropTargetView;
        bool showCollapsedMediaIndicator = isCollapsed && hasMedia;
        bool showCollapsedClipboardIndicator = isCollapsed && !hasMedia && hasClipboard;
        bool showCollapsedScreenshotIndicator = isCollapsed && !hasMedia && !hasClipboard && hasScreenshot;
        bool showCollapsedDropIndicator = isCollapsed && !hasMedia && !hasClipboard && !hasScreenshot && hasActiveDropPreview;
        bool showCollapsedCalendarIndicator = isCollapsed && !hasMedia && !hasClipboard && !hasScreenshot && !hasActiveDropPreview && hasCalendarEvent;

        bool useCalendarTimeInClockHost = !hasMedia && !hasClipboard && !hasScreenshot && !hasActiveDropPreview && hasCalendarEvent;
        UpdateCollapsedClockOrCalendarVisibility(useCalendarTimeInClockHost);

        _animator.UpdateContentMode(
            showMediaView: showMediaView,
            showClipboardView: showClipboardView,
            showScreenshotView: showScreenshotView,
            showDropTargetView: showDropTargetView,
            showDropPromptMode: showDropPromptMode,
            showCalendarView: showCalendarView,
            showClockHoverScreenshotAction: showClockHoverScreenshotAction,
            showExpandedTimeline: showExpandedTimeline,
            showCollapsedMediaIndicator: showCollapsedMediaIndicator,
            showCollapsedClipboardIndicator: showCollapsedClipboardIndicator,
            showCollapsedScreenshotIndicator: showCollapsedScreenshotIndicator,
            showCollapsedDropIndicator: showCollapsedDropIndicator,
            showCollapsedCalendarIndicator: showCollapsedCalendarIndicator,
            showScreenshotHistoryView: showScreenshotHistoryView);
    }

    private void ApplyDragDropStateToView(DragDropState dragDropState, bool animateChange)
    {
        if (dragDropState.HasDropPreview && !string.IsNullOrEmpty(dragDropState.FileName))
        {
            DropFileNameText.Text = dragDropState.FileName;
            string formattedSize = dragDropState.FormattedFileSize;
            DropFileSizeText.Text = formattedSize;
            DropFileSizeText.Visibility = string.IsNullOrEmpty(formattedSize)
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateDropCategoryIcon(dragDropState.Category);
        }
        else if (!dragDropState.HasDropPreview)
        {
            DropFileNameText.Text = string.Empty;
            DropFileSizeText.Text = string.Empty;
            DropFileSizeText.Visibility = Visibility.Collapsed;
            UpdateDropCategoryIcon(DroppedFileCategory.Generic);
        }

        if (animateChange && _controller.CurrentState == NotchState.DropTarget)
        {
            bool showDropPromptMode = dragDropState.IsDragging || !dragDropState.HasDropPreview;
            _animator.AnimateDropSuccessTransition(showDropPromptMode);
        }
    }

    private void UpdateDropCategoryIcon(DroppedFileCategory category)
    {
        DropCategoryGenericIcon.Visibility = category == DroppedFileCategory.Generic ? Visibility.Visible : Visibility.Collapsed;
        DropCategoryImageIcon.Visibility = category == DroppedFileCategory.Image ? Visibility.Visible : Visibility.Collapsed;
        DropCategoryDocumentIcon.Visibility = category == DroppedFileCategory.Document ? Visibility.Visible : Visibility.Collapsed;
        DropCategoryArchiveIcon.Visibility = category == DroppedFileCategory.Archive ? Visibility.Visible : Visibility.Collapsed;
        DropCategoryCodeIcon.Visibility = category == DroppedFileCategory.Code ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateScreenshotHistoryAffordanceVisibility()
    {
        bool hasRecentScreenshots = _controller.HasRecentScreenshots;
        ScreenshotHistoryButton.Visibility = hasRecentScreenshots
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Populates the 1..3 recent screenshot cards and decodes BitmapImage thumbnails (DecodePixelWidth = 208)
    /// only when ScreenshotHistory is actively visible.
    /// </summary>
    private async Task PopulateAndDecodeScreenshotHistoryAsync(bool animateThumbnailsAppear)
    {
        if (_isClosed || _controller.CurrentState != NotchState.ScreenshotHistory)
        {
            return;
        }

        IReadOnlyList<RecentScreenshot> recent = _controller.RecentScreenshots;
        int count = Math.Min(recent.Count, DesignTokens.Screenshot.MaxHistoryCount);
        int version = ++_screenshotHistoryLoadVersion;

        Button[] buttons =
        [
            ScreenshotHistoryItem0Button,
            ScreenshotHistoryItem1Button,
            ScreenshotHistoryItem2Button
        ];

        TextBlock[] timeLabels =
        [
            ScreenshotHistoryItem0TimeText,
            ScreenshotHistoryItem1TimeText,
            ScreenshotHistoryItem2TimeText
        ];

        Image[] images =
        [
            ScreenshotHistoryItem0Image,
            ScreenshotHistoryItem1Image,
            ScreenshotHistoryItem2Image
        ];

        Border[] imageClips =
        [
            ScreenshotHistoryItem0ImageClip,
            ScreenshotHistoryItem1ImageClip,
            ScreenshotHistoryItem2ImageClip
        ];

        FrameworkElement[] fallbackIcons =
        [
            ScreenshotHistoryItem0FallbackIcon,
            ScreenshotHistoryItem1FallbackIcon,
            ScreenshotHistoryItem2FallbackIcon
        ];

        List<FrameworkElement> visibleButtons = new(count);

        for (int i = 0; i < DesignTokens.Screenshot.MaxHistoryCount; i++)
        {
            if (i < count)
            {
                RecentScreenshot item = recent[i];
                buttons[i].Visibility = Visibility.Visible;
                timeLabels[i].Text = item.FormatCaptureTimeLocal();
                AutomationProperties.SetName(buttons[i], item.FormatAccessibleName());
                UpdateHistoryItemBorderBrush(i);
                visibleButtons.Add(buttons[i]);
            }
            else
            {
                buttons[i].Visibility = Visibility.Collapsed;
                timeLabels[i].Text = string.Empty;
                images[i].Source = null;
                imageClips[i].Visibility = Visibility.Collapsed;
                fallbackIcons[i].Visibility = Visibility.Visible;
            }
        }

        if (animateThumbnailsAppear && visibleButtons.Count > 0)
        {
            _animator.AnimateScreenshotHistoryThumbnailsAppear(visibleButtons.ToArray());
        }

        for (int i = 0; i < count; i++)
        {
            if (_isClosed || version != _screenshotHistoryLoadVersion || _controller.CurrentState != NotchState.ScreenshotHistory)
            {
                return;
            }

            await DecodeHistorySlotBitmapAsync(
                recent[i].ImageData,
                images[i],
                imageClips[i],
                fallbackIcons[i],
                version);
        }
    }

    private async Task DecodeHistorySlotBitmapAsync(
        byte[] imageBytes,
        Image targetImage,
        Border targetImageClip,
        FrameworkElement fallbackIcon,
        int expectedVersion)
    {
        if (imageBytes.Length == 0)
        {
            targetImage.Source = null;
            targetImageClip.Visibility = Visibility.Collapsed;
            fallbackIcon.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using InMemoryRandomAccessStream memoryStream = new();
            using (Stream outputStream = memoryStream.AsStreamForWrite())
            {
                await outputStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                await outputStream.FlushAsync();
            }

            memoryStream.Seek(0);

            BitmapImage bitmap = new()
            {
                DecodePixelWidth = DesignTokens.Screenshot.HistoryDecodePixelWidth
            };

            await bitmap.SetSourceAsync(memoryStream);

            if (_isClosed ||
                expectedVersion != _screenshotHistoryLoadVersion ||
                _controller.CurrentState != NotchState.ScreenshotHistory)
            {
                return;
            }

            targetImage.Source = bitmap;
            targetImageClip.Visibility = Visibility.Visible;
            fallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (!_isClosed &&
                expectedVersion == _screenshotHistoryLoadVersion &&
                _controller.CurrentState == NotchState.ScreenshotHistory)
            {
                targetImage.Source = null;
                targetImageClip.Visibility = Visibility.Collapsed;
                fallbackIcon.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>
    /// Releases decoded BitmapImage instances when leaving ScreenshotHistory so only compressed bytes remain in RecentScreenshot.
    /// </summary>
    private void ReleaseDecodedScreenshotHistoryBitmaps()
    {
        _screenshotHistoryLoadVersion++;

        ScreenshotHistoryItem0Image.Source = null;
        ScreenshotHistoryItem0ImageClip.Visibility = Visibility.Collapsed;
        ScreenshotHistoryItem0FallbackIcon.Visibility = Visibility.Visible;

        ScreenshotHistoryItem1Image.Source = null;
        ScreenshotHistoryItem1ImageClip.Visibility = Visibility.Collapsed;
        ScreenshotHistoryItem1FallbackIcon.Visibility = Visibility.Visible;

        ScreenshotHistoryItem2Image.Source = null;
        ScreenshotHistoryItem2ImageClip.Visibility = Visibility.Collapsed;
        ScreenshotHistoryItem2FallbackIcon.Visibility = Visibility.Visible;
    }

    private void ApplyScreenshotStateToView(ScreenshotState screenshotState, bool animateChange)
    {
        if (!screenshotState.IsAvailable || !screenshotState.HasImageBytes)
        {
            _displayedScreenshotWidth = 0;
            _displayedScreenshotHeight = 0;
            ScreenshotDimensionsText.Text = string.Empty;
            ScreenshotDimensionsText.Visibility = Visibility.Collapsed;
            ScreenshotThumbnailHost.Width = DesignTokens.Screenshot.HoverThumbnailMaxWidth;
            _ = UpdateScreenshotImageSourceAsync(null);
            return;
        }

        bool hadPreviousScreenshot = _displayedScreenshotBytes is { Length: > 0 };
        bool contentChanged = _displayedScreenshotWidth != screenshotState.Width
            || _displayedScreenshotHeight != screenshotState.Height
            || !ReferenceEquals(_displayedScreenshotBytes, screenshotState.ImageData);

        _displayedScreenshotWidth = screenshotState.Width;
        _displayedScreenshotHeight = screenshotState.Height;

        double targetThumbnailWidth = Math.Clamp(
            Math.Round(DesignTokens.Screenshot.HoverThumbnailHeight * screenshotState.AspectRatio),
            18.0,
            DesignTokens.Screenshot.HoverThumbnailMaxWidth);
        ScreenshotThumbnailHost.Width = targetThumbnailWidth;

        if (screenshotState.Width > 0 && screenshotState.Height > 0)
        {
            ScreenshotDimensionsText.Text = $"{screenshotState.Width} \u00D7 {screenshotState.Height}";
            ScreenshotDimensionsText.Visibility = Visibility.Visible;
        }
        else
        {
            ScreenshotDimensionsText.Text = string.Empty;
            ScreenshotDimensionsText.Visibility = Visibility.Collapsed;
        }

        _ = UpdateScreenshotImageSourceAsync(screenshotState.ImageData);

        if (animateChange &&
            contentChanged &&
            !_controller.HasActiveMedia &&
            ((hadPreviousScreenshot && _controller.CurrentState == NotchState.Hover) ||
             _controller.CurrentState == NotchState.Screenshot))
        {
            _animator.AnimateActiveScreenshotChange();
        }
    }

    private async Task UpdateScreenshotImageSourceAsync(byte[]? imageBytes)
    {
        if (ReferenceEquals(_displayedScreenshotBytes, imageBytes))
        {
            return;
        }

        _displayedScreenshotBytes = imageBytes;
        int version = ++_screenshotImageLoadVersion;

        if (imageBytes is null || imageBytes.Length == 0)
        {
            ShowScreenshotThumbnailFallback();
            return;
        }

        try
        {
            using InMemoryRandomAccessStream memoryStream = new();
            using (Stream outputStream = memoryStream.AsStreamForWrite())
            {
                await outputStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                await outputStream.FlushAsync();
            }

            memoryStream.Seek(0);

            BitmapImage bitmap = new()
            {
                DecodePixelWidth = (int)(DesignTokens.Screenshot.HoverThumbnailMaxWidth * 2.0)
            };

            await bitmap.SetSourceAsync(memoryStream);

            if (_isClosed || version != _screenshotImageLoadVersion)
            {
                return;
            }

            ScreenshotThumbnailImage.Source = bitmap;
            ScreenshotThumbnailImageClip.Visibility = Visibility.Visible;
            ScreenshotThumbnailFallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (!_isClosed && version == _screenshotImageLoadVersion)
            {
                ShowScreenshotThumbnailFallback();
            }
        }
    }

    private void ShowScreenshotThumbnailFallback()
    {
        ScreenshotThumbnailImage.Source = null;
        ScreenshotThumbnailImageClip.Visibility = Visibility.Collapsed;
        ScreenshotThumbnailFallbackIcon.Visibility = Visibility.Visible;
    }

    private void ApplyClipboardStateToView(ClipboardState clipboardState, bool animateChange)
    {
        if (!clipboardState.IsAvailable || string.IsNullOrEmpty(clipboardState.PreviewText))
        {
            _displayedClipboardPreviewText = string.Empty;
            _displayedClipboardContentType = ClipboardContentType.None;
            ClipboardPreviewText.Text = string.Empty;
            _ = UpdateClipboardImageSourceAsync(null, ClipboardContentType.None);
            return;
        }

        bool hadPreviousClipboard = _displayedClipboardContentType != ClipboardContentType.None;
        bool contentChanged = _displayedClipboardContentType != clipboardState.ContentType
            || !string.Equals(_displayedClipboardPreviewText, clipboardState.PreviewText, StringComparison.Ordinal)
            || !ReferenceEquals(_displayedClipboardImageBytes, clipboardState.ImageData);

        _displayedClipboardContentType = clipboardState.ContentType;
        _displayedClipboardPreviewText = clipboardState.PreviewText;
        ClipboardPreviewText.Text = clipboardState.PreviewText;

        _ = UpdateClipboardImageSourceAsync(clipboardState.ImageData, clipboardState.ContentType);

        if (animateChange &&
            hadPreviousClipboard &&
            contentChanged &&
            !_controller.HasActiveMedia &&
            _controller.CurrentState == NotchState.Hover)
        {
            _animator.AnimateActiveClipboardChange();
        }
    }

    private async Task UpdateClipboardImageSourceAsync(byte[]? imageBytes, ClipboardContentType contentType)
    {
        if (ReferenceEquals(_displayedClipboardImageBytes, imageBytes) &&
            (imageBytes is not null || contentType != ClipboardContentType.Image))
        {
            UpdateClipboardBadgeIcons(contentType, hasDecodedThumbnail: imageBytes is { Length: > 0 });
            return;
        }

        _displayedClipboardImageBytes = imageBytes;
        int version = ++_clipboardImageLoadVersion;

        if (contentType != ClipboardContentType.Image || imageBytes is null || imageBytes.Length == 0)
        {
            ClipboardPreviewImage.Source = null;
            UpdateClipboardBadgeIcons(contentType, hasDecodedThumbnail: false);
            return;
        }

        try
        {
            using InMemoryRandomAccessStream memoryStream = new();
            using (Stream outputStream = memoryStream.AsStreamForWrite())
            {
                await outputStream.WriteAsync(imageBytes, 0, imageBytes.Length);
                await outputStream.FlushAsync();
            }

            memoryStream.Seek(0);

            BitmapImage bitmap = new()
            {
                DecodePixelWidth = (int)(DesignTokens.Clipboard.HoverPreviewSize * 2.0)
            };

            await bitmap.SetSourceAsync(memoryStream);

            if (_isClosed || version != _clipboardImageLoadVersion)
            {
                return;
            }

            ClipboardPreviewImage.Source = bitmap;
            UpdateClipboardBadgeIcons(ClipboardContentType.Image, hasDecodedThumbnail: true);
        }
        catch
        {
            if (!_isClosed && version == _clipboardImageLoadVersion)
            {
                ClipboardPreviewImage.Source = null;
                UpdateClipboardBadgeIcons(ClipboardContentType.Image, hasDecodedThumbnail: false);
            }
        }
    }

    private void UpdateClipboardBadgeIcons(ClipboardContentType contentType, bool hasDecodedThumbnail)
    {
        if (contentType == ClipboardContentType.Image)
        {
            ClipboardTextBadgeIcon.Visibility = Visibility.Collapsed;
            ClipboardImagePreviewClip.Visibility = hasDecodedThumbnail ? Visibility.Visible : Visibility.Collapsed;
            ClipboardImageFallbackIcon.Visibility = hasDecodedThumbnail ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            ClipboardPreviewImage.Source = null;
            ClipboardImagePreviewClip.Visibility = Visibility.Collapsed;
            ClipboardImageFallbackIcon.Visibility = Visibility.Collapsed;
            ClipboardTextBadgeIcon.Visibility = Visibility.Visible;
        }
    }

    private void ApplyMediaMetadataToView(MediaState mediaState, bool animateTrackChange)
    {
        CollapsedMediaIndicator.Foreground = mediaState.IsPlaying ? _accentBrush : _secondaryBrush;
        UpdatePlayPauseIconAndAccessibility(mediaState.IsPlaying);

        if (!mediaState.IsAvailable || mediaState.Track is null)
        {
            _displayedTrackTitle = string.Empty;
            _displayedTrackArtist = string.Empty;
            _ = UpdateArtworkSourceAsync(null);
            ResetTimelineView();
            return;
        }

        MediaTrack track = mediaState.Track;
        bool hadPreviousTrack = !string.IsNullOrEmpty(_displayedTrackTitle);
        bool trackTextChanged = !string.Equals(_displayedTrackTitle, track.Title, StringComparison.Ordinal)
            || !string.Equals(_displayedTrackArtist, track.Artist, StringComparison.Ordinal);

        _displayedTrackTitle = track.Title;
        _displayedTrackArtist = track.Artist;

        MediaTitleText.Text = track.Title;

        if (string.IsNullOrWhiteSpace(track.Artist))
        {
            MediaArtistText.Text = string.Empty;
            MediaArtistText.Visibility = Visibility.Collapsed;
        }
        else
        {
            MediaArtistText.Text = track.Artist;
            MediaArtistText.Visibility = Visibility.Visible;
        }

        _ = UpdateArtworkSourceAsync(track.Artwork);

        if (trackTextChanged && _isSeeking)
        {
            // If the track changed while dragging, cancel the stale drag preview and sync to the new track.
            _isSeeking = false;
            _animator.AnimateSeekThumbState(isActive: _isSeekSurfaceHovered);
        }

        SyncTimelineToView(mediaState);

        if (animateTrackChange &&
            hadPreviousTrack &&
            trackTextChanged &&
            _controller.CurrentState is NotchState.Hover or NotchState.Expanded)
        {
            _animator.AnimateActiveMediaTrackChange();
        }
    }

    private void SyncTimelineToView(MediaState mediaState)
    {
        MediaTimeline? timeline = mediaState.Timeline;
        if (!mediaState.IsAvailable || timeline is null || !timeline.HasValidTimeline)
        {
            ResetTimelineView();
            return;
        }

        TimeSpan duration = timeline.Duration;
        MediaDurationText.Text = FormatMediaTime(duration);

        bool canSeek = timeline.CanSeek && _controller.CurrentState == NotchState.Expanded;
        SeekInteractiveSurface.IsHitTestVisible = canSeek;

        if (_isSeeking)
        {
            return;
        }

        TimeSpan elapsed = timeline.GetInterpolatedElapsed(mediaState.IsPlaying, DateTimeOffset.UtcNow);
        MediaElapsedText.Text = FormatMediaTime(elapsed);

        double normalizedProgress = duration.TotalSeconds > 0.0
            ? Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds, 0.0, 1.0)
            : 0.0;
        TimeSpan remainingDuration = duration > elapsed ? duration - elapsed : TimeSpan.Zero;

        bool isExpandedVisible = _controller.CurrentState == NotchState.Expanded && _controller.HasActiveMedia;
        _animator.SyncSeekTimeline(
            normalizedProgress: normalizedProgress,
            remainingDuration: remainingDuration,
            isExpandedVisible: isExpandedVisible,
            isPlaying: mediaState.IsPlaying,
            hasValidTimeline: true,
            canSeek: timeline.CanSeek);
    }

    private void ResetTimelineView()
    {
        MediaElapsedText.Text = DefaultZeroTimeText;
        MediaDurationText.Text = DefaultZeroTimeText;
        SeekInteractiveSurface.IsHitTestVisible = false;
        _animator.SyncSeekTimeline(
            normalizedProgress: 0.0,
            remainingDuration: TimeSpan.Zero,
            isExpandedVisible: false,
            isPlaying: false,
            hasValidTimeline: false,
            canSeek: false);
    }

    private static string FormatMediaTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return DefaultZeroTimeText;
        }

        int totalHours = (int)time.TotalHours;
        if (totalHours >= 1)
        {
            return $"{totalHours}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        return $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
    }

    private void UpdatePlayPauseIconAndAccessibility(bool isPlaying)
    {
        PlayIconPath.Visibility = isPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseIconPath.Visibility = isPlaying ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(PlayPauseButton, isPlaying ? PauseAccessibleName : PlayAccessibleName);
    }

    private async Task UpdateArtworkSourceAsync(byte[]? artworkBytes)
    {
        if (ReferenceEquals(_displayedArtworkBytes, artworkBytes))
        {
            return;
        }

        _displayedArtworkBytes = artworkBytes;
        int version = ++_artworkLoadVersion;

        if (artworkBytes is null || artworkBytes.Length == 0)
        {
            ShowArtworkFallback();
            return;
        }

        try
        {
            using InMemoryRandomAccessStream memoryStream = new();
            using (Stream outputStream = memoryStream.AsStreamForWrite())
            {
                await outputStream.WriteAsync(artworkBytes, 0, artworkBytes.Length);
                await outputStream.FlushAsync();
            }

            memoryStream.Seek(0);

            BitmapImage bitmap = new()
            {
                DecodePixelWidth = (int)(DesignTokens.Media.ExpandedArtworkSize * 2.0)
            };

            await bitmap.SetSourceAsync(memoryStream);

            if (_isClosed || version != _artworkLoadVersion)
            {
                return;
            }

            MediaArtworkImage.Source = bitmap;
            MediaArtworkImageClip.Visibility = Visibility.Visible;
            MediaArtworkFallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            if (!_isClosed && version == _artworkLoadVersion)
            {
                ShowArtworkFallback();
            }
        }
    }

    private void ShowArtworkFallback()
    {
        MediaArtworkImage.Source = null;
        MediaArtworkImageClip.Visibility = Visibility.Collapsed;
        MediaArtworkFallbackIcon.Visibility = Visibility.Visible;
    }

    private void OnControllerStateChanged(object? sender, NotchStateChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleStateChangedOnUiThread(e);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => HandleStateChangedOnUiThread(e));
        }
    }

    private void HandleStateChangedOnUiThread(NotchStateChangedEventArgs e)
    {
        if (_isClosed || !_controller.IsNotchVisible)
        {
            return;
        }

        NotchDimensions target = e.TargetDimensions;
        double hostWidth = target.HostCanvasWidth;
        double hostHeight = target.HostCanvasHeight;
        bool hasMedia = _controller.HasActiveMedia;
        bool hasClipboard = _controller.HasActiveClipboard;
        bool hasScreenshot = _controller.HasActiveScreenshot;
        bool shouldShowScreenshotInHover = _controller.ShouldShowScreenshotInHover;
        bool hasActiveDrag = _controller.HasActiveDrag;
        bool hasActiveDropPreview = _controller.HasActiveDropPreview;
        bool hasCalendarEvent = _controller.HasActiveCalendarEvent;

        bool useCalendarTimeInClockHost = !hasMedia && !hasClipboard && !hasScreenshot && !hasActiveDropPreview && hasCalendarEvent;
        UpdateCollapsedClockOrCalendarVisibility(useCalendarTimeInClockHost);

        if (e.CurrentState == NotchState.Expanded)
        {
            if (e.PreviousState == NotchState.ScreenshotHistory)
            {
                ReleaseDecodedScreenshotHistoryBitmaps();
            }

            ApplyMediaLayoutForState(NotchState.Expanded);
            _windowManager.SetInteractivePillBounds(target.LogicalWidth, target.LogicalHeight);

            UpdateMediaInteractivity(NotchState.Expanded, hasMedia);
            SyncTimelineToView(_controller.CurrentMediaState);

            _animator.TransitionToState(
                e.PreviousState,
                NotchState.Expanded,
                target,
                hostWidth,
                hostHeight,
                hasMedia,
                hasClipboard,
                hasScreenshot,
                shouldShowScreenshotInHover,
                hasActiveDrag,
                hasActiveDropPreview,
                hasCalendarEvent);
        }
        else if (e.CurrentState == NotchState.ScreenshotHistory)
        {
            ApplyMediaLayoutForState(NotchState.Hover);
            _windowManager.SetInteractivePillBounds(target.LogicalWidth, target.LogicalHeight);

            UpdateMediaInteractivity(NotchState.ScreenshotHistory, hasMedia);
            _animator.StopSeekTimelineInterpolation();
            _ = PopulateAndDecodeScreenshotHistoryAsync(animateThumbnailsAppear: true);

            _animator.TransitionToState(
                e.PreviousState,
                NotchState.ScreenshotHistory,
                target,
                hostWidth,
                hostHeight,
                hasMedia,
                hasClipboard,
                hasScreenshot,
                shouldShowScreenshotInHover,
                hasActiveDrag,
                hasActiveDropPreview,
                hasCalendarEvent);
        }
        else if (e.CurrentState == NotchState.DropTarget)
        {
            if (e.PreviousState == NotchState.ScreenshotHistory)
            {
                ReleaseDecodedScreenshotHistoryBitmaps();
            }

            ApplyMediaLayoutForState(NotchState.Hover);
            _windowManager.SetInteractivePillBounds(target.LogicalWidth, target.LogicalHeight);

            UpdateMediaInteractivity(NotchState.DropTarget, hasMedia);
            _animator.StopSeekTimelineInterpolation();

            _animator.TransitionToState(
                e.PreviousState,
                NotchState.DropTarget,
                target,
                hostWidth,
                hostHeight,
                hasMedia,
                hasClipboard,
                hasScreenshot,
                shouldShowScreenshotInHover,
                hasActiveDrag,
                hasActiveDropPreview,
                hasCalendarEvent);
        }
        else if (e.CurrentState == NotchState.Hover)
        {
            ApplyMediaLayoutForState(NotchState.Hover);
            UpdateMediaInteractivity(NotchState.Hover, hasMedia);
            _animator.StopSeekTimelineInterpolation();

            bool leavingScreenshotHistory = e.PreviousState == NotchState.ScreenshotHistory;
            _windowManager.SetInteractivePillBounds(target.LogicalWidth, target.LogicalHeight);

            _animator.TransitionToState(
                e.PreviousState,
                NotchState.Hover,
                target,
                hostWidth,
                hostHeight,
                hasMedia,
                hasClipboard,
                hasScreenshot,
                shouldShowScreenshotInHover,
                hasActiveDrag,
                hasActiveDropPreview,
                hasCalendarEvent,
                onCompleted: leavingScreenshotHistory
                    ? () =>
                    {
                        if (DispatcherQueue.HasThreadAccess)
                        {
                            if (_controller.CurrentState != NotchState.ScreenshotHistory)
                            {
                                ReleaseDecodedScreenshotHistoryBitmaps();
                            }
                        }
                        else
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_controller.CurrentState != NotchState.ScreenshotHistory)
                                {
                                    ReleaseDecodedScreenshotHistoryBitmaps();
                                }
                            });
                        }
                    }
                    : null);
        }
        else if (NotchController.IsCollapsedState(e.CurrentState))
        {
            UpdateMediaInteractivity(e.CurrentState, hasMedia);
            _animator.StopSeekTimelineInterpolation();

            if (!e.IsGeometryTransition)
            {
                // Collapsed Idle <-> Collapsed Media <-> Collapsed Clipboard <-> Collapsed Screenshot <-> Collapsed Calendar transition: pill geometry remains 140x30.
                SyncActiveContentMode();
                return;
            }

            bool leavingScreenshotHistory = e.PreviousState == NotchState.ScreenshotHistory;
            _windowManager.SetInteractivePillBounds(target.LogicalWidth, target.LogicalHeight);

            _animator.TransitionToState(
                e.PreviousState,
                e.CurrentState,
                target,
                hostWidth,
                hostHeight,
                hasMedia,
                hasClipboard,
                hasScreenshot,
                shouldShowScreenshotInHover,
                hasActiveDrag,
                hasActiveDropPreview,
                hasCalendarEvent,
                onCompleted: leavingScreenshotHistory
                    ? () =>
                    {
                        if (DispatcherQueue.HasThreadAccess)
                        {
                            if (_controller.CurrentState != NotchState.ScreenshotHistory)
                            {
                                ReleaseDecodedScreenshotHistoryBitmaps();
                            }
                        }
                        else
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (_controller.CurrentState != NotchState.ScreenshotHistory)
                                {
                                    ReleaseDecodedScreenshotHistoryBitmaps();
                                }
                            });
                        }
                    }
                    : null);
        }
    }

    private void ApplyHostCanvasBounds(double hostWidth, double hostHeight)
    {
        ShadowLayer.Width = hostWidth;
        ShadowLayer.Height = hostHeight;
        NotchSurface.Width = hostWidth;
        NotchSurface.Height = hostHeight;
        ContentHost.Width = hostWidth;
        ContentHost.Height = hostHeight;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _windowManager.EnsureStylesOnActivation();

        if (_hasCompletedInitialActivation)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                _controller.RequestCalendarRefresh();
            }

            return;
        }

        _hasCompletedInitialActivation = true;
        NotchDimensions dimensions = _controller.GetDimensionsForCurrentState();
        _windowManager.UpdatePositionAndSize(dimensions.HostCanvasWidth, dimensions.HostCanvasHeight);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _artworkLoadVersion++;
        _clipboardImageLoadVersion++;
        _screenshotImageLoadVersion++;
        ReleaseDecodedScreenshotHistoryBitmaps();

        DetachTransportButtonHandlers(PreviousTrackButton);
        DetachTransportButtonHandlers(PlayPauseButton);
        DetachTransportButtonHandlers(NextTrackButton);
        DetachTransportButtonHandlers(ClockScreenshotButton);
        DetachTransportButtonHandlers(ClipboardScreenshotButton);
        DetachTransportButtonHandlers(CalendarScreenshotButton);
        DetachTransportButtonHandlers(ScreenshotHistoryButton);
        DetachTransportButtonHandlers(ScreenshotRecaptureButton);

        DetachHistoryItemHandlers(ScreenshotHistoryItem0Button);
        DetachHistoryItemHandlers(ScreenshotHistoryItem1Button);
        DetachHistoryItemHandlers(ScreenshotHistoryItem2Button);

        NotchSurface.KeyDown -= OnNotchSurfaceKeyDown;

        _windowManager.ClipboardMessageReceived -= OnWindowManagerClipboardMessageReceived;
        _windowManager.NativeHotkeyPressed -= OnWindowManagerNativeHotkeyPressed;
        _windowManager.NativeTrayMessageReceived -= OnWindowManagerNativeTrayMessageReceived;
        _windowManager.WindowCloseRequested -= OnWindowManagerWindowCloseRequested;
        _windowManager.CursorOutsideInteractiveBoundsDetected -= OnWindowManagerCursorOutsideInteractiveBoundsDetected;
        _controller.StateChanged -= OnControllerStateChanged;
        _controller.NotchVisibilityChanged -= OnControllerNotchVisibilityChanged;
        _controller.ApplicationExitRequested -= OnControllerApplicationExitRequested;
        _controller.TimeTextChanged -= OnControllerTimeTextChanged;
        _controller.MediaStateChanged -= OnControllerMediaStateChanged;
        _controller.ClipboardStateChanged -= OnControllerClipboardStateChanged;
        _controller.ScreenshotStateChanged -= OnControllerScreenshotStateChanged;
        _controller.RecentScreenshotsChanged -= OnControllerRecentScreenshotsChanged;
        _controller.DragDropStateChanged -= OnControllerDragDropStateChanged;
        _controller.CalendarStateChanged -= OnControllerCalendarStateChanged;
        Activated -= OnWindowActivated;
        Closed -= OnWindowClosed;

        _controller.Dispose();
        _animator.Dispose();
        _windowManager.Dispose();
    }
}
