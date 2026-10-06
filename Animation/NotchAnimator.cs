using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using WindowsNotch.Core;
using WindowsNotch.Notch;

namespace WindowsNotch.Animation;

public sealed record NotchVisualLayers(
    FrameworkElement ShadowLayer,
    FrameworkElement SurfaceContainer,
    FrameworkElement BackdropLayer,
    FrameworkElement SurfaceBaseLayer,
    FrameworkElement InnerDepthLayer,
    FrameworkElement BorderLayer,
    FrameworkElement ContentHost,
    FrameworkElement ClockViewHost,
    FrameworkElement CollapsedMediaIndicator,
    FrameworkElement CollapsedClipboardIndicator,
    FrameworkElement CollapsedScreenshotIndicator,
    FrameworkElement CollapsedDropIndicator,
    FrameworkElement CollapsedCalendarIndicator,
    FrameworkElement ClockHoverScreenshotActionHost,
    TextBlock PrimaryClockText,
    TextBlock SecondaryClockText,
    FrameworkElement MediaViewHost,
    FrameworkElement MediaControlsHost,
    FrameworkElement MediaTimelineRow,
    FrameworkElement SeekTrackBackground,
    FrameworkElement SeekTrackFill,
    FrameworkElement SeekThumb,
    FrameworkElement ClipboardViewHost,
    FrameworkElement ScreenshotViewHost,
    FrameworkElement ScreenshotHistoryViewHost,
    FrameworkElement DropTargetViewHost,
    FrameworkElement DropTargetPromptPanel,
    FrameworkElement DropPreviewContentPanel,
    FrameworkElement CalendarViewHost);

/// <summary>
/// Compositor-driven animation and visual layer engine for the persistent Notch surface.
/// Coordinates geometry morphing (Collapsed ↔ Hover ↔ DropTarget ↔ ScreenshotHistory ↔ Expanded), soft shadow, glass backdrop with solid fallback,
/// inner depth sheen, 1px vector pill border, minute-boundary clock transitions,
/// Clock ↔ Media ↔ Clipboard ↔ Screenshot ↔ ScreenshotHistory ↔ DropTarget ↔ Calendar content transitions, transport button micro-interactions, and Composition-only timeline progress interpolation.
/// </summary>
public sealed class NotchAnimator : IDisposable
{
    private readonly NotchVisualLayers _layers;

    private readonly Visual _surfaceVisual;
    private readonly Visual _surfaceBaseVisual;
    private readonly Visual _innerDepthVisual;
    private readonly Visual _contentVisual;
    private readonly Visual _clockViewVisual;
    private readonly Visual _collapsedMediaIndicatorVisual;
    private readonly Visual _collapsedClipboardIndicatorVisual;
    private readonly Visual _collapsedScreenshotIndicatorVisual;
    private readonly Visual _collapsedDropIndicatorVisual;
    private readonly Visual _collapsedCalendarIndicatorVisual;
    private readonly Visual _clockHoverScreenshotActionVisual;
    private readonly Visual _primaryClockVisual;
    private readonly Visual _secondaryClockVisual;
    private readonly Visual _mediaViewVisual;
    private readonly Visual _mediaControlsVisual;
    private readonly Visual _mediaTimelineVisual;
    private readonly Visual _seekTrackBackgroundVisual;
    private readonly Visual _seekTrackFillVisual;
    private readonly Visual _seekThumbVisual;
    private readonly Visual _clipboardViewVisual;
    private readonly Visual _screenshotViewVisual;
    private readonly Visual _screenshotHistoryViewVisual;
    private readonly Visual _dropTargetViewVisual;
    private readonly Visual _dropTargetPromptVisual;
    private readonly Visual _dropPreviewContentVisual;
    private readonly Visual _calendarViewVisual;

    private readonly Compositor _compositor;
    private readonly CompositionRoundedRectangleGeometry _pillGeometry;
    private readonly CompositionGeometricClip _geometricClip;

    private readonly CompositionPropertySet _seekProgressState;
    private readonly CompositionRoundedRectangleGeometry _seekFillClipGeometry;
    private readonly CompositionGeometricClip _seekFillGeometricClip;
    private readonly ExpressionAnimation _seekFillSizeExpression;
    private readonly ExpressionAnimation _seekThumbTranslationExpression;
    private readonly LinearEasingFunction _linearEasing;

    private readonly SpriteVisual _shadowVisual;
    private readonly DropShadow _dropShadow;
    private readonly ExpressionAnimation _shadowSizeExpression;
    private readonly ExpressionAnimation _shadowOffsetExpression;

    private readonly SpriteVisual? _glassBackdropVisual;
    private readonly CompositionBrush? _glassBackdropBrush;
    private readonly bool _hasGlassBackdrop;

    private readonly ShapeVisual _borderShapeVisual;
    private readonly CompositionRoundedRectangleGeometry _borderGeometry;
    private readonly CompositionSpriteShape _borderSpriteShape;
    private readonly CompositionLinearGradientBrush _borderStrokeBrush;
    private readonly ExpressionAnimation _borderSizeExpression;
    private readonly ExpressionAnimation _borderOffsetExpression;
    private readonly ExpressionAnimation _borderRadiusExpression;

    private readonly ExpressionAnimation _pillOffsetExpression;
    private readonly ExpressionAnimation _clockTranslationExpression;
    private readonly CubicBezierEasingFunction _collapseEasing;

    private double _currentTargetPillHeight = DesignTokens.NotchSize.CollapsedHeight;
    private bool _isPrimaryClockActive = true;
    private bool _isClockViewVisible = true;
    private bool _isMediaViewVisible;
    private bool _isClipboardViewVisible;
    private bool _isScreenshotViewVisible;
    private bool _isScreenshotHistoryViewVisible;
    private bool _isDropTargetViewVisible;
    private bool _isDropPromptModeVisible = true;
    private bool _isCalendarViewVisible;
    private bool _isTimelineRowVisible;
    private bool _isCollapsedIndicatorVisible;
    private bool _isCollapsedClipboardIndicatorVisible;
    private bool _isCollapsedScreenshotIndicatorVisible;
    private bool _isCollapsedDropIndicatorVisible;
    private bool _isCollapsedCalendarIndicatorVisible;
    private bool _isClockHoverActionVisible;
    private int _activeTransitionVersion;
    private bool _isDisposed;

    public NotchAnimator(NotchVisualLayers layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        _layers = layers;

        ElementCompositionPreview.SetIsTranslationEnabled(_layers.ContentHost, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_layers.ClockViewHost, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_layers.PrimaryClockText, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_layers.SecondaryClockText, true);
        ElementCompositionPreview.SetIsTranslationEnabled(_layers.SeekThumb, true);

        _surfaceVisual = ElementCompositionPreview.GetElementVisual(_layers.SurfaceContainer);
        _surfaceBaseVisual = ElementCompositionPreview.GetElementVisual(_layers.SurfaceBaseLayer);
        _innerDepthVisual = ElementCompositionPreview.GetElementVisual(_layers.InnerDepthLayer);
        _contentVisual = ElementCompositionPreview.GetElementVisual(_layers.ContentHost);
        _clockViewVisual = ElementCompositionPreview.GetElementVisual(_layers.ClockViewHost);
        _collapsedMediaIndicatorVisual = ElementCompositionPreview.GetElementVisual(_layers.CollapsedMediaIndicator);
        _collapsedClipboardIndicatorVisual = ElementCompositionPreview.GetElementVisual(_layers.CollapsedClipboardIndicator);
        _collapsedScreenshotIndicatorVisual = ElementCompositionPreview.GetElementVisual(_layers.CollapsedScreenshotIndicator);
        _collapsedDropIndicatorVisual = ElementCompositionPreview.GetElementVisual(_layers.CollapsedDropIndicator);
        _collapsedCalendarIndicatorVisual = ElementCompositionPreview.GetElementVisual(_layers.CollapsedCalendarIndicator);
        _clockHoverScreenshotActionVisual = ElementCompositionPreview.GetElementVisual(_layers.ClockHoverScreenshotActionHost);
        _primaryClockVisual = ElementCompositionPreview.GetElementVisual(_layers.PrimaryClockText);
        _secondaryClockVisual = ElementCompositionPreview.GetElementVisual(_layers.SecondaryClockText);
        _mediaViewVisual = ElementCompositionPreview.GetElementVisual(_layers.MediaViewHost);
        _mediaControlsVisual = ElementCompositionPreview.GetElementVisual(_layers.MediaControlsHost);
        _mediaTimelineVisual = ElementCompositionPreview.GetElementVisual(_layers.MediaTimelineRow);
        _seekTrackBackgroundVisual = ElementCompositionPreview.GetElementVisual(_layers.SeekTrackBackground);
        _seekTrackFillVisual = ElementCompositionPreview.GetElementVisual(_layers.SeekTrackFill);
        _seekThumbVisual = ElementCompositionPreview.GetElementVisual(_layers.SeekThumb);
        _clipboardViewVisual = ElementCompositionPreview.GetElementVisual(_layers.ClipboardViewHost);
        _screenshotViewVisual = ElementCompositionPreview.GetElementVisual(_layers.ScreenshotViewHost);
        _screenshotHistoryViewVisual = ElementCompositionPreview.GetElementVisual(_layers.ScreenshotHistoryViewHost);
        _dropTargetViewVisual = ElementCompositionPreview.GetElementVisual(_layers.DropTargetViewHost);
        _dropTargetPromptVisual = ElementCompositionPreview.GetElementVisual(_layers.DropTargetPromptPanel);
        _dropPreviewContentVisual = ElementCompositionPreview.GetElementVisual(_layers.DropPreviewContentPanel);
        _calendarViewVisual = ElementCompositionPreview.GetElementVisual(_layers.CalendarViewHost);
        _compositor = _surfaceVisual.Compositor;

        _pillGeometry = _compositor.CreateRoundedRectangleGeometry();
        _geometricClip = _compositor.CreateGeometricClip(_pillGeometry);
        _surfaceVisual.Clip = _geometricClip;

        // 1. Pill horizontal centering & clock vertical centering inside the active pill height.
        // ContentHost stays at (0,0) inside the top-aligned 452x172 canvas so every child view's XAML hit-testing
        // matches its visual coordinates 1:1 without any translation mismatch.
        _pillOffsetExpression = _compositor.CreateExpressionAnimation(
            "Vector2((canvasWidth - pill.Size.X) * 0.5f, 0.0f)");
        _pillOffsetExpression.SetReferenceParameter("pill", _pillGeometry);

        _clockTranslationExpression = _compositor.CreateExpressionAnimation(
            "Vector3(0.0f, Max(0.0f, (pill.Size.Y - clockHeight) * 0.5f), 0.0f)");
        _clockTranslationExpression.SetReferenceParameter("pill", _pillGeometry);
        _clockTranslationExpression.SetScalarParameter("clockHeight", (float)DesignTokens.NotchSize.CollapsedHeight);
        _clockViewVisual.StartAnimation("Translation", _clockTranslationExpression);

        // 2. Soft Shadow layer bound to the morphing pill geometry (inset inside corner radius so rectangular
        // DropShadow never pokes dark sharp corners outside the rounded pill silhouette).
        _dropShadow = _compositor.CreateDropShadow();
        _dropShadow.Color = DesignTokens.Colors.Background;

        _shadowVisual = _compositor.CreateSpriteVisual();
        _shadowVisual.Shadow = _dropShadow;
        ElementCompositionPreview.SetElementChildVisual(_layers.ShadowLayer, _shadowVisual);

        _shadowSizeExpression = _compositor.CreateExpressionAnimation(
            "Vector2(Max(0.0f, pill.Size.X - pill.CornerRadius.X * 1.1f), Max(0.0f, pill.Size.Y - pill.CornerRadius.Y * 0.85f))");
        _shadowSizeExpression.SetReferenceParameter("pill", _pillGeometry);

        _shadowOffsetExpression = _compositor.CreateExpressionAnimation(
            "Vector3(pill.Offset.X + pill.CornerRadius.X * 0.55f, pill.Offset.Y + pill.CornerRadius.Y * 0.425f, 0.0f)");
        _shadowOffsetExpression.SetReferenceParameter("pill", _pillGeometry);

        _shadowVisual.StartAnimation("Size", _shadowSizeExpression);
        _shadowVisual.StartAnimation("Offset", _shadowOffsetExpression);

        // 3. Native Composition glass backdrop with graceful fallback.
        (_glassBackdropVisual, _glassBackdropBrush, _hasGlassBackdrop) = TryCreateGlassBackdrop();

        // 4. Subtle 1px vector pill border tracking _pillGeometry on the compositor thread.
        float strokeWidth = DesignTokens.Surface.BorderStrokeThickness;
        float halfStroke = strokeWidth * 0.5f;

        _borderGeometry = _compositor.CreateRoundedRectangleGeometry();
        _borderSizeExpression = _compositor.CreateExpressionAnimation(
            "pill.Size - Vector2(strokeWidth, strokeWidth)");
        _borderSizeExpression.SetReferenceParameter("pill", _pillGeometry);
        _borderSizeExpression.SetScalarParameter("strokeWidth", strokeWidth);

        _borderOffsetExpression = _compositor.CreateExpressionAnimation(
            "pill.Offset + Vector2(halfStroke, halfStroke)");
        _borderOffsetExpression.SetReferenceParameter("pill", _pillGeometry);
        _borderOffsetExpression.SetScalarParameter("halfStroke", halfStroke);

        _borderRadiusExpression = _compositor.CreateExpressionAnimation(
            "pill.CornerRadius - Vector2(halfStroke, halfStroke)");
        _borderRadiusExpression.SetReferenceParameter("pill", _pillGeometry);
        _borderRadiusExpression.SetScalarParameter("halfStroke", halfStroke);

        _borderGeometry.StartAnimation("Size", _borderSizeExpression);
        _borderGeometry.StartAnimation("Offset", _borderOffsetExpression);
        _borderGeometry.StartAnimation("CornerRadius", _borderRadiusExpression);

        _borderStrokeBrush = CreateBorderHighlightBrush();
        _borderSpriteShape = _compositor.CreateSpriteShape(_borderGeometry);
        _borderSpriteShape.StrokeThickness = strokeWidth;
        _borderSpriteShape.StrokeBrush = _borderStrokeBrush;

        _borderShapeVisual = _compositor.CreateShapeVisual();
        _borderShapeVisual.Shapes.Add(_borderSpriteShape);
        ElementCompositionPreview.SetElementChildVisual(_layers.BorderLayer, _borderShapeVisual);

        // 5. Compositor-driven seek progress bar & thumb expressions (zero timers / zero polling).
        _seekProgressState = _compositor.CreatePropertySet();
        _seekProgressState.InsertScalar("Progress", 0.0f);
        _seekProgressState.InsertScalar("TrackWidth", 0.0f);

        float trackHeight = (float)DesignTokens.Media.SeekTrackHeight;
        float trackRadius = (float)DesignTokens.Media.SeekTrackCornerRadius;

        _seekFillClipGeometry = _compositor.CreateRoundedRectangleGeometry();
        _seekFillClipGeometry.CornerRadius = new Vector2(trackRadius, trackRadius);
        _seekFillGeometricClip = _compositor.CreateGeometricClip(_seekFillClipGeometry);
        _seekTrackFillVisual.Clip = _seekFillGeometricClip;

        _seekFillSizeExpression = _compositor.CreateExpressionAnimation(
            "Vector2( Clamp(seek.Progress, 0.0f, 1.0f) * seek.TrackWidth, trackHeight )");
        _seekFillSizeExpression.SetReferenceParameter("seek", _seekProgressState);
        _seekFillSizeExpression.SetScalarParameter("trackHeight", trackHeight);
        _seekFillClipGeometry.StartAnimation("Size", _seekFillSizeExpression);

        _seekThumbTranslationExpression = _compositor.CreateExpressionAnimation(
            "Vector3( Clamp(seek.Progress, 0.0f, 1.0f) * seek.TrackWidth, 0.0f, 0.0f )");
        _seekThumbTranslationExpression.SetReferenceParameter("seek", _seekProgressState);
        _seekThumbVisual.StartAnimation("Translation", _seekThumbTranslationExpression);

        float thumbHalfSize = (float)(DesignTokens.Media.SeekThumbSize * 0.5);
        _seekThumbVisual.CenterPoint = new Vector3(thumbHalfSize, thumbHalfSize, 0.0f);

        _linearEasing = _compositor.CreateLinearEasingFunction();
        _collapseEasing = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(DesignTokens.Animation.CollapseBezierX1, DesignTokens.Animation.CollapseBezierY1),
            new Vector2(DesignTokens.Animation.CollapseBezierX2, DesignTokens.Animation.CollapseBezierY2));

        _layers.ContentHost.SizeChanged += OnContentSizeChanged;
        _layers.ClockViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.ClockHoverScreenshotActionHost.SizeChanged += OnSubContentSizeChanged;
        _layers.MediaViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.MediaControlsHost.SizeChanged += OnSubContentSizeChanged;
        _layers.MediaTimelineRow.SizeChanged += OnSubContentSizeChanged;
        _layers.ClipboardViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.ScreenshotViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.ScreenshotHistoryViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.DropTargetViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.DropTargetPromptPanel.SizeChanged += OnSubContentSizeChanged;
        _layers.DropPreviewContentPanel.SizeChanged += OnSubContentSizeChanged;
        _layers.CalendarViewHost.SizeChanged += OnSubContentSizeChanged;
        _layers.SeekTrackBackground.SizeChanged += OnSeekTrackSizeChanged;
    }

    public void InitializeRestState(
        NotchDimensions initialDimensions,
        string initialTimeText,
        bool hasActiveMedia,
        bool hasActiveClipboard,
        bool hasActiveScreenshot,
        bool hasActiveDropPreview = false,
        bool hasActiveCalendarEvent = false)
    {
        float width = (float)initialDimensions.LogicalWidth;
        float height = (float)initialDimensions.LogicalHeight;
        float radius = (float)initialDimensions.CornerRadius;
        _currentTargetPillHeight = initialDimensions.LogicalHeight;

        _pillGeometry.Size = new Vector2(width, height);
        _pillGeometry.CornerRadius = new Vector2(radius, radius);

        _surfaceBaseVisual.Opacity = ResolveEffectiveSurfaceOpacity(initialDimensions.SurfaceOpacity);
        _innerDepthVisual.Opacity = initialDimensions.InnerDepthOpacity;
        _borderShapeVisual.Opacity = initialDimensions.BorderOpacity;

        _dropShadow.BlurRadius = initialDimensions.ShadowBlurRadius;
        _dropShadow.Opacity = initialDimensions.ShadowOpacity;
        _dropShadow.Offset = new Vector3(0.0f, initialDimensions.ShadowOffsetY, 0.0f);

        UpdateContentCenterPoint();
        UpdateSubContentCenterPoints();

        _contentVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        _contentVisual.Scale = new Vector3(
            initialDimensions.ContentScale,
            initialDimensions.ContentScale,
            1.0f);
        _contentVisual.Opacity = initialDimensions.ContentOpacity;

        _isPrimaryClockActive = true;
        _layers.PrimaryClockText.Text = initialTimeText;
        _layers.SecondaryClockText.Text = initialTimeText;

        _primaryClockVisual.Opacity = 1.0f;
        _primaryClockVisual.Properties.InsertVector3("Translation", Vector3.Zero);

        _secondaryClockVisual.Opacity = 0.0f;
        _secondaryClockVisual.Properties.InsertVector3(
            "Translation",
            new Vector3(0.0f, DesignTokens.Animation.MinuteChangeSlideOffsetPx, 0.0f));

        Vector3 collapsedSubScale = new(
            DesignTokens.Animation.ContentSwitchScale,
            DesignTokens.Animation.ContentSwitchScale,
            1.0f);

        _isClockViewVisible = true;
        _isMediaViewVisible = false;
        _isClipboardViewVisible = false;
        _isScreenshotViewVisible = false;
        _isScreenshotHistoryViewVisible = false;
        _isDropTargetViewVisible = false;
        _isDropPromptModeVisible = true;
        _isCalendarViewVisible = false;
        _isTimelineRowVisible = false;
        _isClockHoverActionVisible = false;
        _clockViewVisual.Opacity = 1.0f;
        _clockViewVisual.Scale = Vector3.One;

        _layers.ClockHoverScreenshotActionHost.Visibility = Visibility.Collapsed;
        _clockHoverScreenshotActionVisual.Opacity = 0.0f;

        _mediaViewVisual.Opacity = 0.0f;
        _mediaViewVisual.Scale = collapsedSubScale;

        _mediaControlsVisual.Opacity = 0.0f;
        _mediaControlsVisual.Scale = collapsedSubScale;

        _mediaTimelineVisual.Opacity = 0.0f;
        _mediaTimelineVisual.Scale = collapsedSubScale;

        _clipboardViewVisual.Opacity = 0.0f;
        _clipboardViewVisual.Scale = collapsedSubScale;

        _screenshotViewVisual.Opacity = 0.0f;
        _screenshotViewVisual.Scale = collapsedSubScale;

        _screenshotHistoryViewVisual.Opacity = 0.0f;
        _screenshotHistoryViewVisual.Scale = collapsedSubScale;

        _dropTargetViewVisual.Opacity = 0.0f;
        _dropTargetViewVisual.Scale = collapsedSubScale;

        _dropTargetPromptVisual.Opacity = 1.0f;
        _dropTargetPromptVisual.Scale = Vector3.One;

        _dropPreviewContentVisual.Opacity = 0.0f;
        _dropPreviewContentVisual.Scale = collapsedSubScale;

        _calendarViewVisual.Opacity = 0.0f;
        _calendarViewVisual.Scale = collapsedSubScale;

        _seekTrackBackgroundVisual.Opacity = DesignTokens.Media.SeekTrackInactiveOpacity;
        _seekTrackFillVisual.Opacity = DesignTokens.Media.SeekTrackActiveOpacity;
        _seekThumbVisual.Opacity = 0.0f;
        _seekThumbVisual.Scale = Vector3.One;

        _isCollapsedIndicatorVisible = hasActiveMedia;
        _layers.CollapsedMediaIndicator.Visibility = hasActiveMedia ? Visibility.Visible : Visibility.Collapsed;
        _collapsedMediaIndicatorVisual.Opacity = hasActiveMedia
            ? DesignTokens.Media.CollapsedIndicatorOpacity
            : 0.0f;

        bool showCollapsedClipboard = !hasActiveMedia && hasActiveClipboard;
        _isCollapsedClipboardIndicatorVisible = showCollapsedClipboard;
        _layers.CollapsedClipboardIndicator.Visibility = showCollapsedClipboard
            ? Visibility.Visible
            : Visibility.Collapsed;
        _collapsedClipboardIndicatorVisual.Opacity = showCollapsedClipboard
            ? DesignTokens.Clipboard.CollapsedIndicatorOpacity
            : 0.0f;

        bool showCollapsedScreenshot = !hasActiveMedia && !hasActiveClipboard && hasActiveScreenshot;
        _isCollapsedScreenshotIndicatorVisible = showCollapsedScreenshot;
        _layers.CollapsedScreenshotIndicator.Visibility = showCollapsedScreenshot
            ? Visibility.Visible
            : Visibility.Collapsed;
        _collapsedScreenshotIndicatorVisual.Opacity = showCollapsedScreenshot
            ? DesignTokens.Screenshot.CollapsedIndicatorOpacity
            : 0.0f;

        bool showCollapsedDrop = !hasActiveMedia && !hasActiveClipboard && !hasActiveScreenshot && hasActiveDropPreview;
        _isCollapsedDropIndicatorVisible = showCollapsedDrop;
        _layers.CollapsedDropIndicator.Visibility = showCollapsedDrop
            ? Visibility.Visible
            : Visibility.Collapsed;
        _collapsedDropIndicatorVisual.Opacity = showCollapsedDrop
            ? DesignTokens.DragDrop.CollapsedIndicatorOpacity
            : 0.0f;

        bool showCollapsedCalendar = !hasActiveMedia && !hasActiveClipboard && !hasActiveScreenshot && !hasActiveDropPreview && hasActiveCalendarEvent;
        _isCollapsedCalendarIndicatorVisible = showCollapsedCalendar;
        _layers.CollapsedCalendarIndicator.Visibility = showCollapsedCalendar
            ? Visibility.Visible
            : Visibility.Collapsed;
        _collapsedCalendarIndicatorVisual.Opacity = showCollapsedCalendar
            ? DesignTokens.Calendar.CollapsedIndicatorOpacity
            : 0.0f;

        UpdateHostCanvasSize(initialDimensions.HostCanvasWidth, initialDimensions.HostCanvasHeight);
    }

    public void InitializeMediaControlButton(
        FrameworkElement buttonElement,
        FrameworkElement backgroundElement,
        FrameworkElement iconElement)
    {
        if (_isDisposed)
        {
            return;
        }

        Visual buttonVisual = ElementCompositionPreview.GetElementVisual(buttonElement);
        Visual backgroundVisual = ElementCompositionPreview.GetElementVisual(backgroundElement);
        Visual iconVisual = ElementCompositionPreview.GetElementVisual(iconElement);

        float halfSize = (float)(DesignTokens.Media.ControlButtonSize * 0.5);
        buttonVisual.CenterPoint = new Vector3(halfSize, halfSize, 0.0f);
        buttonVisual.Scale = Vector3.One;

        backgroundVisual.Opacity = DesignTokens.Media.ControlBackgroundRestOpacity;
        iconVisual.Opacity = DesignTokens.Media.ControlIconRestOpacity;
    }

    public void AnimateMediaControlButtonState(
        FrameworkElement buttonElement,
        FrameworkElement backgroundElement,
        FrameworkElement iconElement,
        bool isHovered,
        bool isPressed)
    {
        if (_isDisposed)
        {
            return;
        }

        Visual buttonVisual = ElementCompositionPreview.GetElementVisual(buttonElement);
        Visual backgroundVisual = ElementCompositionPreview.GetElementVisual(backgroundElement);
        Visual iconVisual = ElementCompositionPreview.GetElementVisual(iconElement);

        float halfSize = (float)(DesignTokens.Media.ControlButtonSize * 0.5);
        buttonVisual.CenterPoint = new Vector3(halfSize, halfSize, 0.0f);

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Media.ControlHoverDurationMs);

        float targetBgOpacity = isPressed
            ? DesignTokens.Media.ControlBackgroundPressedOpacity
            : (isHovered
                ? DesignTokens.Media.ControlBackgroundHoverOpacity
                : DesignTokens.Media.ControlBackgroundRestOpacity);

        float targetIconOpacity = (isHovered || isPressed)
            ? DesignTokens.Media.ControlIconActiveOpacity
            : DesignTokens.Media.ControlIconRestOpacity;

        float targetScaleFactor = isPressed ? DesignTokens.Media.ControlPressedScale : 1.0f;
        Vector3 targetScale = new(targetScaleFactor, targetScaleFactor, 1.0f);

        ScalarKeyFrameAnimation bgOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        bgOpacityAnim.Duration = duration;
        bgOpacityAnim.InsertKeyFrame(1.0f, targetBgOpacity, _collapseEasing);

        ScalarKeyFrameAnimation iconOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        iconOpacityAnim.Duration = duration;
        iconOpacityAnim.InsertKeyFrame(1.0f, targetIconOpacity, _collapseEasing);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(1.0f, targetScale, _collapseEasing);

        backgroundVisual.StartAnimation("Opacity", bgOpacityAnim);
        iconVisual.StartAnimation("Opacity", iconOpacityAnim);
        buttonVisual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>
    /// Updates the seek bar position from an authoritative Windows timeline snapshot, and starts a
    /// lightweight Composition linear animation only when the expanded timeline is visible and playing.
    /// </summary>
    public void SyncSeekTimeline(
        double normalizedProgress,
        TimeSpan remainingDuration,
        bool isExpandedVisible,
        bool isPlaying,
        bool hasValidTimeline,
        bool canSeek)
    {
        if (_isDisposed)
        {
            return;
        }

        float clampedProgress = (float)Math.Clamp(normalizedProgress, 0.0, 1.0);
        _seekProgressState.StopAnimation("Progress");
        _seekProgressState.InsertScalar("Progress", clampedProgress);

        _seekTrackFillVisual.Opacity = hasValidTimeline
            ? DesignTokens.Media.SeekTrackActiveOpacity
            : 0.0f;
        _seekThumbVisual.Opacity = (hasValidTimeline && canSeek) ? 1.0f : 0.0f;

        if (isExpandedVisible &&
            isPlaying &&
            hasValidTimeline &&
            clampedProgress < 1.0f &&
            remainingDuration > TimeSpan.FromMilliseconds(50))
        {
            ScalarKeyFrameAnimation progressAnim = _compositor.CreateScalarKeyFrameAnimation();
            progressAnim.Duration = remainingDuration;
            progressAnim.InsertKeyFrame(0.0f, clampedProgress, _linearEasing);
            progressAnim.InsertKeyFrame(1.0f, 1.0f, _linearEasing);
            _seekProgressState.StartAnimation("Progress", progressAnim);
        }
    }

    /// <summary>
    /// Stops any active Composition timeline interpolation immediately (e.g., when leaving Expanded state).
    /// </summary>
    public void StopSeekTimelineInterpolation()
    {
        if (_isDisposed)
        {
            return;
        }

        _seekProgressState.StopAnimation("Progress");
    }

    /// <summary>
    /// Updates the visual seek preview position immediately during user drag without mutating MediaState.
    /// </summary>
    public void SetSeekPreviewProgress(double normalizedProgress)
    {
        if (_isDisposed)
        {
            return;
        }

        float clampedProgress = (float)Math.Clamp(normalizedProgress, 0.0, 1.0);
        _seekProgressState.StopAnimation("Progress");
        _seekProgressState.InsertScalar("Progress", clampedProgress);
    }

    public void AnimateSeekThumbState(bool isActive)
    {
        if (_isDisposed)
        {
            return;
        }

        float scaleFactor = isActive
            ? DesignTokens.Media.SeekThumbActiveScale
            : DesignTokens.Media.SeekThumbRestScale;
        Vector3 targetScale = new(scaleFactor, scaleFactor, 1.0f);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = TimeSpan.FromMilliseconds(DesignTokens.Media.ControlHoverDurationMs);
        scaleAnim.InsertKeyFrame(1.0f, targetScale, _collapseEasing);
        _seekThumbVisual.StartAnimation("Scale", scaleAnim);
    }

    public void UpdateHostCanvasSize(double canvasWidth, double canvasHeight)
    {
        if (_isDisposed)
        {
            return;
        }

        Vector2 canvasSize = new((float)canvasWidth, (float)canvasHeight);

        _borderShapeVisual.Size = canvasSize;
        if (_glassBackdropVisual is not null)
        {
            _glassBackdropVisual.Size = canvasSize;
        }

        _pillOffsetExpression.SetScalarParameter("canvasWidth", (float)canvasWidth);
        _pillGeometry.StartAnimation("Offset", _pillOffsetExpression);
    }

    /// <summary>
    /// Animates only the inner clock text (outgoing text fades + translates slightly up,
    /// incoming text fades in + settles from slightly below) without affecting the Notch container.
    /// </summary>
    public void AnimateClockMinuteTransition(string newTimeText)
    {
        if (_isDisposed)
        {
            return;
        }

        TextBlock outgoingElement = _isPrimaryClockActive ? _layers.PrimaryClockText : _layers.SecondaryClockText;
        TextBlock incomingElement = _isPrimaryClockActive ? _layers.SecondaryClockText : _layers.PrimaryClockText;
        Visual outgoingVisual = _isPrimaryClockActive ? _primaryClockVisual : _secondaryClockVisual;
        Visual incomingVisual = _isPrimaryClockActive ? _secondaryClockVisual : _primaryClockVisual;

        if (string.Equals(outgoingElement.Text, newTimeText, StringComparison.Ordinal))
        {
            return;
        }

        incomingElement.Text = newTimeText;
        _isPrimaryClockActive = !_isPrimaryClockActive;

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Animation.MinuteChangeDurationMs);
        float offset = DesignTokens.Animation.MinuteChangeSlideOffsetPx;

        ScalarKeyFrameAnimation fadeOut = _compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = duration;
        fadeOut.InsertKeyFrame(0.0f, 1.0f);
        fadeOut.InsertKeyFrame(1.0f, 0.0f, _collapseEasing);

        Vector3KeyFrameAnimation slideUpOut = _compositor.CreateVector3KeyFrameAnimation();
        slideUpOut.Duration = duration;
        slideUpOut.InsertKeyFrame(0.0f, Vector3.Zero);
        slideUpOut.InsertKeyFrame(1.0f, new Vector3(0.0f, -offset, 0.0f), _collapseEasing);

        ScalarKeyFrameAnimation fadeIn = _compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = duration;
        fadeIn.InsertKeyFrame(0.0f, 0.0f);
        fadeIn.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

        Vector3KeyFrameAnimation slideUpIn = _compositor.CreateVector3KeyFrameAnimation();
        slideUpIn.Duration = duration;
        slideUpIn.InsertKeyFrame(0.0f, new Vector3(0.0f, offset, 0.0f));
        slideUpIn.InsertKeyFrame(1.0f, Vector3.Zero, _collapseEasing);

        outgoingVisual.StartAnimation("Opacity", fadeOut);
        outgoingVisual.StartAnimation("Translation", slideUpOut);

        incomingVisual.StartAnimation("Opacity", fadeIn);
        incomingVisual.StartAnimation("Translation", slideUpIn);
    }

    /// <summary>
    /// Smoothly cross-fades and micro-scales between the Clock view, Media Hover view,
    /// Clipboard Hover view, Screenshot Preview Hover view, DropTarget view, Calendar Hover view, and Expanded Media Timeline row.
    /// </summary>
    public void UpdateContentMode(
        bool showMediaView,
        bool showClipboardView,
        bool showScreenshotView,
        bool showDropTargetView,
        bool showDropPromptMode,
        bool showCalendarView,
        bool showClockHoverScreenshotAction,
        bool showExpandedTimeline,
        bool showCollapsedMediaIndicator,
        bool showCollapsedClipboardIndicator,
        bool showCollapsedScreenshotIndicator,
        bool showCollapsedDropIndicator,
        bool showCollapsedCalendarIndicator,
        bool showScreenshotHistoryView = false)
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateSubContentCenterPoints();

        bool showClockView = !showMediaView &&
                             !showClipboardView &&
                             !showScreenshotView &&
                             !showScreenshotHistoryView &&
                             !showDropTargetView &&
                             !showCalendarView;
        int durationMs = (showMediaView || showClipboardView || showScreenshotView || showScreenshotHistoryView || showDropTargetView || showCalendarView)
            ? DesignTokens.Animation.ContentSwitchDurationMs
            : DesignTokens.Animation.ContentSwitchDisappearDurationMs;
        TimeSpan switchDuration = TimeSpan.FromMilliseconds(durationMs);
        float switchScale = DesignTokens.Animation.ContentSwitchScale;

        if (_isCollapsedIndicatorVisible != showCollapsedMediaIndicator)
        {
            _isCollapsedIndicatorVisible = showCollapsedMediaIndicator;
            _layers.CollapsedMediaIndicator.Visibility = showCollapsedMediaIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;

            ScalarKeyFrameAnimation indicatorFade = _compositor.CreateScalarKeyFrameAnimation();
            indicatorFade.Duration = switchDuration;
            indicatorFade.InsertKeyFrame(
                1.0f,
                showCollapsedMediaIndicator ? DesignTokens.Media.CollapsedIndicatorOpacity : 0.0f,
                _collapseEasing);
            _collapsedMediaIndicatorVisual.StartAnimation("Opacity", indicatorFade);
        }

        if (_isCollapsedClipboardIndicatorVisible != showCollapsedClipboardIndicator)
        {
            _isCollapsedClipboardIndicatorVisible = showCollapsedClipboardIndicator;
            _layers.CollapsedClipboardIndicator.Visibility = showCollapsedClipboardIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;

            ScalarKeyFrameAnimation clipboardIndicatorFade = _compositor.CreateScalarKeyFrameAnimation();
            clipboardIndicatorFade.Duration = switchDuration;
            clipboardIndicatorFade.InsertKeyFrame(
                1.0f,
                showCollapsedClipboardIndicator ? DesignTokens.Clipboard.CollapsedIndicatorOpacity : 0.0f,
                _collapseEasing);
            _collapsedClipboardIndicatorVisual.StartAnimation("Opacity", clipboardIndicatorFade);
        }

        if (_isCollapsedScreenshotIndicatorVisible != showCollapsedScreenshotIndicator)
        {
            _isCollapsedScreenshotIndicatorVisible = showCollapsedScreenshotIndicator;
            _layers.CollapsedScreenshotIndicator.Visibility = showCollapsedScreenshotIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;

            ScalarKeyFrameAnimation screenshotIndicatorFade = _compositor.CreateScalarKeyFrameAnimation();
            screenshotIndicatorFade.Duration = switchDuration;
            screenshotIndicatorFade.InsertKeyFrame(
                1.0f,
                showCollapsedScreenshotIndicator ? DesignTokens.Screenshot.CollapsedIndicatorOpacity : 0.0f,
                _collapseEasing);
            _collapsedScreenshotIndicatorVisual.StartAnimation("Opacity", screenshotIndicatorFade);
        }

        if (_isCollapsedDropIndicatorVisible != showCollapsedDropIndicator)
        {
            _isCollapsedDropIndicatorVisible = showCollapsedDropIndicator;
            _layers.CollapsedDropIndicator.Visibility = showCollapsedDropIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;

            ScalarKeyFrameAnimation dropIndicatorFade = _compositor.CreateScalarKeyFrameAnimation();
            dropIndicatorFade.Duration = switchDuration;
            dropIndicatorFade.InsertKeyFrame(
                1.0f,
                showCollapsedDropIndicator ? DesignTokens.DragDrop.CollapsedIndicatorOpacity : 0.0f,
                _collapseEasing);
            _collapsedDropIndicatorVisual.StartAnimation("Opacity", dropIndicatorFade);
        }

        if (_isCollapsedCalendarIndicatorVisible != showCollapsedCalendarIndicator)
        {
            _isCollapsedCalendarIndicatorVisible = showCollapsedCalendarIndicator;
            _layers.CollapsedCalendarIndicator.Visibility = showCollapsedCalendarIndicator
                ? Visibility.Visible
                : Visibility.Collapsed;

            ScalarKeyFrameAnimation calendarIndicatorFade = _compositor.CreateScalarKeyFrameAnimation();
            calendarIndicatorFade.Duration = switchDuration;
            calendarIndicatorFade.InsertKeyFrame(
                1.0f,
                showCollapsedCalendarIndicator ? DesignTokens.Calendar.CollapsedIndicatorOpacity : 0.0f,
                _collapseEasing);
            _collapsedCalendarIndicatorVisual.StartAnimation("Opacity", calendarIndicatorFade);
        }

        if (_isClockHoverActionVisible != showClockHoverScreenshotAction)
        {
            _isClockHoverActionVisible = showClockHoverScreenshotAction;
            _layers.ClockHoverScreenshotActionHost.Visibility = showClockHoverScreenshotAction
                ? Visibility.Visible
                : Visibility.Collapsed;

            ScalarKeyFrameAnimation clockActionFade = _compositor.CreateScalarKeyFrameAnimation();
            clockActionFade.Duration = switchDuration;
            clockActionFade.InsertKeyFrame(1.0f, showClockHoverScreenshotAction ? 1.0f : 0.0f, _collapseEasing);

            Vector3KeyFrameAnimation clockActionScale = _compositor.CreateVector3KeyFrameAnimation();
            clockActionScale.Duration = switchDuration;
            clockActionScale.InsertKeyFrame(
                1.0f,
                showClockHoverScreenshotAction ? Vector3.One : new Vector3(switchScale, switchScale, 1.0f),
                _collapseEasing);

            _clockHoverScreenshotActionVisual.StartAnimation("Opacity", clockActionFade);
            _clockHoverScreenshotActionVisual.StartAnimation("Scale", clockActionScale);
        }

        if (_isTimelineRowVisible != showExpandedTimeline)
        {
            _isTimelineRowVisible = showExpandedTimeline;
            if (!showExpandedTimeline)
            {
                StopSeekTimelineInterpolation();
            }

            TimeSpan timelineDuration = TimeSpan.FromMilliseconds(
                showExpandedTimeline
                    ? DesignTokens.Animation.ContentSwitchDurationMs
                    : DesignTokens.Animation.ContentSwitchDisappearDurationMs);

            ScalarKeyFrameAnimation timelineOpacity = _compositor.CreateScalarKeyFrameAnimation();
            timelineOpacity.Duration = timelineDuration;
            timelineOpacity.InsertKeyFrame(1.0f, showExpandedTimeline ? 1.0f : 0.0f, _collapseEasing);

            Vector3KeyFrameAnimation timelineScale = _compositor.CreateVector3KeyFrameAnimation();
            timelineScale.Duration = timelineDuration;
            timelineScale.InsertKeyFrame(
                1.0f,
                showExpandedTimeline ? Vector3.One : new Vector3(switchScale, switchScale, 1.0f),
                _collapseEasing);

            _mediaTimelineVisual.StartAnimation("Opacity", timelineOpacity);
            _mediaTimelineVisual.StartAnimation("Scale", timelineScale);
        }

        if (_isClockViewVisible != showClockView)
        {
            _isClockViewVisible = showClockView;

            float clockTargetOpacity = showClockView ? 1.0f : 0.0f;
            Vector3 clockTargetScale = showClockView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation clockOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            clockOpacityAnim.Duration = switchDuration;
            clockOpacityAnim.InsertKeyFrame(1.0f, clockTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation clockScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            clockScaleAnim.Duration = switchDuration;
            clockScaleAnim.InsertKeyFrame(1.0f, clockTargetScale, _collapseEasing);

            _clockViewVisual.StartAnimation("Opacity", clockOpacityAnim);
            _clockViewVisual.StartAnimation("Scale", clockScaleAnim);
        }

        if (_isMediaViewVisible != showMediaView)
        {
            _isMediaViewVisible = showMediaView;

            float mediaTargetOpacity = showMediaView ? 1.0f : 0.0f;
            Vector3 mediaTargetScale = showMediaView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation mediaOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            mediaOpacityAnim.Duration = switchDuration;
            mediaOpacityAnim.InsertKeyFrame(1.0f, mediaTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation mediaScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            mediaScaleAnim.Duration = switchDuration;
            mediaScaleAnim.InsertKeyFrame(1.0f, mediaTargetScale, _collapseEasing);

            _mediaViewVisual.StartAnimation("Opacity", mediaOpacityAnim);
            _mediaViewVisual.StartAnimation("Scale", mediaScaleAnim);

            _mediaControlsVisual.StartAnimation("Opacity", mediaOpacityAnim);
            _mediaControlsVisual.StartAnimation("Scale", mediaScaleAnim);
        }

        if (_isClipboardViewVisible != showClipboardView)
        {
            _isClipboardViewVisible = showClipboardView;

            TimeSpan clipboardDuration = TimeSpan.FromMilliseconds(
                showClipboardView
                    ? DesignTokens.Clipboard.ContentTransitionDurationMs
                    : DesignTokens.Animation.ContentSwitchDisappearDurationMs);

            float clipboardTargetOpacity = showClipboardView ? 1.0f : 0.0f;
            Vector3 clipboardTargetScale = showClipboardView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation clipboardOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            clipboardOpacityAnim.Duration = clipboardDuration;
            clipboardOpacityAnim.InsertKeyFrame(1.0f, clipboardTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation clipboardScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            clipboardScaleAnim.Duration = clipboardDuration;
            clipboardScaleAnim.InsertKeyFrame(1.0f, clipboardTargetScale, _collapseEasing);

            _clipboardViewVisual.StartAnimation("Opacity", clipboardOpacityAnim);
            _clipboardViewVisual.StartAnimation("Scale", clipboardScaleAnim);
        }

        if (_isScreenshotViewVisible != showScreenshotView)
        {
            _isScreenshotViewVisible = showScreenshotView;

            TimeSpan screenshotDuration = TimeSpan.FromMilliseconds(
                showScreenshotView
                    ? DesignTokens.Screenshot.ContentTransitionDurationMs
                    : DesignTokens.Animation.ContentSwitchDisappearDurationMs);

            float screenshotTargetOpacity = showScreenshotView ? 1.0f : 0.0f;
            Vector3 screenshotTargetScale = showScreenshotView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation screenshotOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            screenshotOpacityAnim.Duration = screenshotDuration;
            screenshotOpacityAnim.InsertKeyFrame(1.0f, screenshotTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation screenshotScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            screenshotScaleAnim.Duration = screenshotDuration;
            screenshotScaleAnim.InsertKeyFrame(1.0f, screenshotTargetScale, _collapseEasing);

            _screenshotViewVisual.StartAnimation("Opacity", screenshotOpacityAnim);
            _screenshotViewVisual.StartAnimation("Scale", screenshotScaleAnim);
        }

        if (_isScreenshotHistoryViewVisible != showScreenshotHistoryView)
        {
            _isScreenshotHistoryViewVisible = showScreenshotHistoryView;

            TimeSpan screenshotHistoryDuration = TimeSpan.FromMilliseconds(
                showScreenshotHistoryView
                    ? DesignTokens.Screenshot.HistoryAnimationDurationMs
                    : DesignTokens.Screenshot.HistoryExitAnimationDurationMs);

            float screenshotHistoryTargetOpacity = showScreenshotHistoryView ? 1.0f : 0.0f;
            Vector3 screenshotHistoryTargetScale = showScreenshotHistoryView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation screenshotHistoryOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            screenshotHistoryOpacityAnim.Duration = screenshotHistoryDuration;
            screenshotHistoryOpacityAnim.InsertKeyFrame(1.0f, screenshotHistoryTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation screenshotHistoryScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            screenshotHistoryScaleAnim.Duration = screenshotHistoryDuration;
            screenshotHistoryScaleAnim.InsertKeyFrame(1.0f, screenshotHistoryTargetScale, _collapseEasing);

            _screenshotHistoryViewVisual.StartAnimation("Opacity", screenshotHistoryOpacityAnim);
            _screenshotHistoryViewVisual.StartAnimation("Scale", screenshotHistoryScaleAnim);
        }

        if (showDropTargetView)
        {
            AnimateDropSubMode(showDropPromptMode, animateSubMode: _isDropTargetViewVisible && _isDropPromptModeVisible != showDropPromptMode);
        }

        if (_isDropTargetViewVisible != showDropTargetView)
        {
            _isDropTargetViewVisible = showDropTargetView;

            TimeSpan dropViewDuration = TimeSpan.FromMilliseconds(
                showDropTargetView
                    ? DesignTokens.Animation.DragEnterDurationMs
                    : DesignTokens.Animation.ContentSwitchDisappearDurationMs);

            float dropTargetOpacity = showDropTargetView ? 1.0f : 0.0f;
            Vector3 dropTargetScale = showDropTargetView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation dropOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            dropOpacityAnim.Duration = dropViewDuration;
            dropOpacityAnim.InsertKeyFrame(1.0f, dropTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation dropScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            dropScaleAnim.Duration = dropViewDuration;
            dropScaleAnim.InsertKeyFrame(1.0f, dropTargetScale, _collapseEasing);

            _dropTargetViewVisual.StartAnimation("Opacity", dropOpacityAnim);
            _dropTargetViewVisual.StartAnimation("Scale", dropScaleAnim);
        }

        if (_isCalendarViewVisible != showCalendarView)
        {
            _isCalendarViewVisible = showCalendarView;

            TimeSpan calendarDuration = TimeSpan.FromMilliseconds(
                showCalendarView
                    ? DesignTokens.Calendar.ContentTransitionDurationMs
                    : DesignTokens.Animation.ContentSwitchDisappearDurationMs);

            float calendarTargetOpacity = showCalendarView ? 1.0f : 0.0f;
            Vector3 calendarTargetScale = showCalendarView
                ? Vector3.One
                : new Vector3(switchScale, switchScale, 1.0f);

            ScalarKeyFrameAnimation calendarOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            calendarOpacityAnim.Duration = calendarDuration;
            calendarOpacityAnim.InsertKeyFrame(1.0f, calendarTargetOpacity, _collapseEasing);

            Vector3KeyFrameAnimation calendarScaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            calendarScaleAnim.Duration = calendarDuration;
            calendarScaleAnim.InsertKeyFrame(1.0f, calendarTargetScale, _collapseEasing);

            _calendarViewVisual.StartAnimation("Opacity", calendarOpacityAnim);
            _calendarViewVisual.StartAnimation("Scale", calendarScaleAnim);
        }
    }

    /// <summary>
    /// Initializes a screenshot history thumbnail button visual for compositor scale and opacity animations.
    /// </summary>
    public void InitializeScreenshotHistoryItem(FrameworkElement itemElement)
    {
        if (_isDisposed)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(itemElement);
        visual.CenterPoint = new Vector3(
            (float)((itemElement.ActualWidth > 0 ? itemElement.ActualWidth : DesignTokens.Screenshot.HistoryThumbnailWidth) * 0.5),
            (float)((itemElement.ActualHeight > 0 ? itemElement.ActualHeight : DesignTokens.Screenshot.HistoryThumbnailHeight) * 0.5),
            0.0f);
        visual.Scale = Vector3.One;
        visual.Opacity = 1.0f;
    }

    /// <summary>
    /// Animates visible recent screenshot thumbnails in (0.96 -> 1.0 scale + fade over 160ms) when entering ScreenshotHistory.
    /// </summary>
    public void AnimateScreenshotHistoryThumbnailsAppear(ReadOnlySpan<FrameworkElement> visibleItems)
    {
        if (_isDisposed)
        {
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Screenshot.HistoryThumbnailAppearDurationMs);
        float appearScale = DesignTokens.Screenshot.HistoryThumbnailAppearScale;
        Vector3 initialScale = new(appearScale, appearScale, 1.0f);

        foreach (FrameworkElement itemElement in visibleItems)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(itemElement);
            visual.CenterPoint = new Vector3(
                (float)((itemElement.ActualWidth > 0 ? itemElement.ActualWidth : DesignTokens.Screenshot.HistoryThumbnailWidth) * 0.5),
                (float)((itemElement.ActualHeight > 0 ? itemElement.ActualHeight : DesignTokens.Screenshot.HistoryThumbnailHeight) * 0.5),
                0.0f);

            ScalarKeyFrameAnimation opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.Duration = duration;
            opacityAnim.InsertKeyFrame(0.0f, 0.0f);
            opacityAnim.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

            Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(0.0f, initialScale);
            scaleAnim.InsertKeyFrame(1.0f, Vector3.One, _collapseEasing);

            visual.StartAnimation("Opacity", opacityAnim);
            visual.StartAnimation("Scale", scaleAnim);
        }
    }

    /// <summary>
    /// Animates hover/press/selection feedback on a recent screenshot history thumbnail over 140ms.
    /// </summary>
    public void AnimateScreenshotHistoryItemInteraction(
        FrameworkElement itemElement,
        bool isHovered,
        bool isPressed)
    {
        if (_isDisposed)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(itemElement);
        visual.CenterPoint = new Vector3(
            (float)((itemElement.ActualWidth > 0 ? itemElement.ActualWidth : DesignTokens.Screenshot.HistoryThumbnailWidth) * 0.5),
            (float)((itemElement.ActualHeight > 0 ? itemElement.ActualHeight : DesignTokens.Screenshot.HistoryThumbnailHeight) * 0.5),
            0.0f);

        float targetScale = isPressed
            ? DesignTokens.Screenshot.HistoryThumbnailPressedScale
            : isHovered
                ? DesignTokens.Screenshot.HistoryThumbnailHoverScale
                : 1.0f;

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Screenshot.HistorySelectionAnimationDurationMs);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(1.0f, new Vector3(targetScale, targetScale, 1.0f), _collapseEasing);

        visual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>
    /// Animates the 160ms drop success transition from the "Drop file here" prompt to the dropped file preview,
    /// or settles a newly dropped file preview when already in DropTarget preview mode.
    /// </summary>
    public void AnimateDropSuccessTransition(bool showDropPromptMode)
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateSubContentCenterPoints();

        if (!_isDropTargetViewVisible)
        {
            AnimateDropSubMode(showDropPromptMode, animateSubMode: false);
            return;
        }

        if (_isDropPromptModeVisible != showDropPromptMode)
        {
            AnimateDropSubMode(showDropPromptMode, animateSubMode: true);
            return;
        }

        if (!showDropPromptMode)
        {
            // Another file was dropped while the preview was already visible: perform a subtle 160ms scale/opacity settle.
            TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Animation.DropSuccessDurationMs);
            float switchScale = DesignTokens.Animation.ContentSwitchScale;

            ScalarKeyFrameAnimation fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
            fadeAnim.Duration = duration;
            fadeAnim.InsertKeyFrame(0.0f, DesignTokens.Animation.IdleContentOpacity);
            fadeAnim.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

            Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.Duration = duration;
            scaleAnim.InsertKeyFrame(0.0f, new Vector3(switchScale, switchScale, 1.0f));
            scaleAnim.InsertKeyFrame(1.0f, Vector3.One, _collapseEasing);

            _dropPreviewContentVisual.StartAnimation("Opacity", fadeAnim);
            _dropPreviewContentVisual.StartAnimation("Scale", scaleAnim);
        }
    }

    private void AnimateDropSubMode(bool showDropPromptMode, bool animateSubMode)
    {
        _isDropPromptModeVisible = showDropPromptMode;
        float switchScale = DesignTokens.Animation.ContentSwitchScale;
        Vector3 collapsedScale = new(switchScale, switchScale, 1.0f);

        if (!animateSubMode)
        {
            _dropTargetPromptVisual.StopAnimation("Opacity");
            _dropTargetPromptVisual.StopAnimation("Scale");
            _dropPreviewContentVisual.StopAnimation("Opacity");
            _dropPreviewContentVisual.StopAnimation("Scale");

            _dropTargetPromptVisual.Opacity = showDropPromptMode ? 1.0f : 0.0f;
            _dropTargetPromptVisual.Scale = showDropPromptMode ? Vector3.One : collapsedScale;

            _dropPreviewContentVisual.Opacity = showDropPromptMode ? 0.0f : 1.0f;
            _dropPreviewContentVisual.Scale = showDropPromptMode ? collapsedScale : Vector3.One;
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(
            showDropPromptMode
                ? DesignTokens.Animation.DragEnterDurationMs
                : DesignTokens.Animation.DropSuccessDurationMs);

        ScalarKeyFrameAnimation promptOpacity = _compositor.CreateScalarKeyFrameAnimation();
        promptOpacity.Duration = duration;
        promptOpacity.InsertKeyFrame(1.0f, showDropPromptMode ? 1.0f : 0.0f, _collapseEasing);

        Vector3KeyFrameAnimation promptScale = _compositor.CreateVector3KeyFrameAnimation();
        promptScale.Duration = duration;
        promptScale.InsertKeyFrame(1.0f, showDropPromptMode ? Vector3.One : collapsedScale, _collapseEasing);

        ScalarKeyFrameAnimation previewOpacity = _compositor.CreateScalarKeyFrameAnimation();
        previewOpacity.Duration = duration;
        previewOpacity.InsertKeyFrame(1.0f, showDropPromptMode ? 0.0f : 1.0f, _collapseEasing);

        Vector3KeyFrameAnimation previewScale = _compositor.CreateVector3KeyFrameAnimation();
        previewScale.Duration = duration;
        previewScale.InsertKeyFrame(1.0f, showDropPromptMode ? collapsedScale : Vector3.One, _collapseEasing);

        _dropTargetPromptVisual.StartAnimation("Opacity", promptOpacity);
        _dropTargetPromptVisual.StartAnimation("Scale", promptScale);
        _dropPreviewContentVisual.StartAnimation("Opacity", previewOpacity);
        _dropPreviewContentVisual.StartAnimation("Scale", previewScale);
    }

    /// <summary>
    /// Performs a subtle compositor fade/scale settle when track metadata changes while Media view is visible.
    /// </summary>
    public void AnimateActiveMediaTrackChange()
    {
        if (_isDisposed || !_isMediaViewVisible)
        {
            return;
        }

        UpdateSubContentCenterPoints();

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Animation.ContentSwitchDurationMs);
        float switchScale = DesignTokens.Animation.ContentSwitchScale;

        ScalarKeyFrameAnimation fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Duration = duration;
        fadeAnim.InsertKeyFrame(0.0f, DesignTokens.Animation.IdleContentOpacity);
        fadeAnim.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(0.0f, new Vector3(switchScale, switchScale, 1.0f));
        scaleAnim.InsertKeyFrame(1.0f, Vector3.One, _collapseEasing);

        _mediaViewVisual.StartAnimation("Opacity", fadeAnim);
        _mediaViewVisual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>
    /// Performs a subtle 180ms compositor fade/scale settle when clipboard content updates while the Clipboard Hover view is visible.
    /// </summary>
    public void AnimateActiveClipboardChange()
    {
        if (_isDisposed || !_isClipboardViewVisible)
        {
            return;
        }

        UpdateSubContentCenterPoints();

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Clipboard.ContentTransitionDurationMs);
        float switchScale = DesignTokens.Animation.ContentSwitchScale;

        ScalarKeyFrameAnimation fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Duration = duration;
        fadeAnim.InsertKeyFrame(0.0f, DesignTokens.Animation.IdleContentOpacity);
        fadeAnim.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(0.0f, new Vector3(switchScale, switchScale, 1.0f));
        scaleAnim.InsertKeyFrame(1.0f, Vector3.One, _collapseEasing);

        _clipboardViewVisual.StartAnimation("Opacity", fadeAnim);
        _clipboardViewVisual.StartAnimation("Scale", scaleAnim);
    }

    /// <summary>
    /// Performs a subtle 180ms compositor fade/scale settle when a new screenshot is captured while the Screenshot Preview view or collapsed screenshot indicator is visible.
    /// </summary>
    public void AnimateActiveScreenshotChange()
    {
        if (_isDisposed || (!_isScreenshotViewVisible && !_isCollapsedScreenshotIndicatorVisible))
        {
            return;
        }

        UpdateSubContentCenterPoints();

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Screenshot.ContentTransitionDurationMs);
        float switchScale = DesignTokens.Animation.ContentSwitchScale;

        ScalarKeyFrameAnimation fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Duration = duration;
        fadeAnim.InsertKeyFrame(0.0f, DesignTokens.Animation.IdleContentOpacity);
        fadeAnim.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(0.0f, new Vector3(switchScale, switchScale, 1.0f));
        scaleAnim.InsertKeyFrame(1.0f, Vector3.One, _collapseEasing);

        if (_isScreenshotViewVisible)
        {
            _screenshotViewVisual.StartAnimation("Opacity", fadeAnim);
            _screenshotViewVisual.StartAnimation("Scale", scaleAnim);
        }

        if (_isCollapsedScreenshotIndicatorVisible)
        {
            _collapsedScreenshotIndicatorVisual.StartAnimation("Opacity", fadeAnim);
            _collapsedScreenshotIndicatorVisual.StartAnimation("Scale", scaleAnim);
        }
    }

    /// <summary>
    /// Performs a subtle 180ms compositor fade/scale settle when the upcoming calendar event updates while the Calendar Hover view is visible.
    /// </summary>
    public void AnimateActiveCalendarChange()
    {
        if (_isDisposed || !_isCalendarViewVisible)
        {
            return;
        }

        UpdateSubContentCenterPoints();

        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Calendar.ContentTransitionDurationMs);
        float switchScale = DesignTokens.Animation.ContentSwitchScale;

        ScalarKeyFrameAnimation fadeAnim = _compositor.CreateScalarKeyFrameAnimation();
        fadeAnim.Duration = duration;
        fadeAnim.InsertKeyFrame(0.0f, DesignTokens.Animation.IdleContentOpacity);
        fadeAnim.InsertKeyFrame(1.0f, 1.0f, _collapseEasing);

        Vector3KeyFrameAnimation scaleAnim = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnim.Duration = duration;
        scaleAnim.InsertKeyFrame(0.0f, new Vector3(switchScale, switchScale, 1.0f));
        scaleAnim.InsertKeyFrame(1.0f, Vector3.One, _collapseEasing);

        _calendarViewVisual.StartAnimation("Opacity", fadeAnim);
        _calendarViewVisual.StartAnimation("Scale", scaleAnim);
    }

    public void TransitionToState(
        NotchState previousState,
        NotchState targetState,
        NotchDimensions targetDimensions,
        double hostCanvasWidth,
        double hostCanvasHeight,
        bool hasActiveMedia,
        bool hasActiveClipboard,
        bool hasActiveScreenshot,
        bool shouldShowScreenshotInHover,
        bool hasActiveDrag,
        bool hasActiveDropPreview,
        bool hasActiveCalendarEvent = false,
        Action? onCompleted = null)
    {
        if (_isDisposed)
        {
            return;
        }

        int transitionVersion = ++_activeTransitionVersion;
        _currentTargetPillHeight = targetDimensions.LogicalHeight;

        UpdateHostCanvasSize(hostCanvasWidth, hostCanvasHeight);
        UpdateContentCenterPoint();

        bool showDropTargetView = targetState == NotchState.DropTarget;
        bool showDropPromptMode = hasActiveDrag || !hasActiveDropPreview;
        bool showScreenshotHistoryView = !showDropTargetView && targetState == NotchState.ScreenshotHistory;
        bool showMediaView = !showDropTargetView &&
                             !showScreenshotHistoryView &&
                             hasActiveMedia &&
                             targetState is NotchState.Hover or NotchState.Expanded;
        bool showScreenshotView = !showDropTargetView &&
                                  !showScreenshotHistoryView &&
                                  !hasActiveMedia &&
                                  hasActiveScreenshot &&
                                  targetState == NotchState.Hover &&
                                  (!hasActiveClipboard || shouldShowScreenshotInHover);
        bool showClipboardView = !showDropTargetView &&
                                 !showScreenshotHistoryView &&
                                 !hasActiveMedia &&
                                 !showScreenshotView &&
                                 hasActiveClipboard &&
                                 targetState == NotchState.Hover;
        bool showCalendarView = !showDropTargetView &&
                                !showScreenshotHistoryView &&
                                !hasActiveMedia &&
                                !showScreenshotView &&
                                !showClipboardView &&
                                hasActiveCalendarEvent &&
                                targetState == NotchState.Hover;
        bool showClockHoverScreenshotAction = !showDropTargetView &&
                                              !showScreenshotHistoryView &&
                                              !hasActiveMedia &&
                                              !showClipboardView &&
                                              !showScreenshotView &&
                                              !showCalendarView &&
                                              targetState == NotchState.Hover;
        bool showExpandedTimeline = hasActiveMedia && targetState == NotchState.Expanded;
        bool isCollapsed = NotchController.IsCollapsedState(targetState);
        bool showCollapsedMediaIndicator = hasActiveMedia && isCollapsed;
        bool showCollapsedClipboardIndicator = !hasActiveMedia && hasActiveClipboard && isCollapsed;
        bool showCollapsedScreenshotIndicator = !hasActiveMedia && !hasActiveClipboard && hasActiveScreenshot && isCollapsed;
        bool showCollapsedDropIndicator = !hasActiveMedia && !hasActiveClipboard && !hasActiveScreenshot && hasActiveDropPreview && isCollapsed;
        bool showCollapsedCalendarIndicator = !hasActiveMedia && !hasActiveClipboard && !hasActiveScreenshot && !hasActiveDropPreview && hasActiveCalendarEvent && isCollapsed;

        UpdateContentMode(
            showMediaView,
            showClipboardView,
            showScreenshotView,
            showDropTargetView,
            showDropPromptMode,
            showCalendarView,
            showClockHoverScreenshotAction,
            showExpandedTimeline,
            showCollapsedMediaIndicator,
            showCollapsedClipboardIndicator,
            showCollapsedScreenshotIndicator,
            showCollapsedDropIndicator,
            showCollapsedCalendarIndicator,
            showScreenshotHistoryView);

        CompositionScopedBatch? batch = null;
        if (onCompleted is not null)
        {
            batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        }

        if (targetState == NotchState.Expanded)
        {
            StartExpandedAnimations(targetDimensions);
        }
        else if (targetState == NotchState.ScreenshotHistory)
        {
            StartScreenshotHistoryExpansionAnimations(targetDimensions);
        }
        else if (targetState == NotchState.DropTarget)
        {
            StartDropTargetExpansionAnimations(targetDimensions);
        }
        else if (targetState == NotchState.Hover)
        {
            if (previousState == NotchState.Expanded)
            {
                StartCollapseKeyFrameAnimations(
                    targetDimensions,
                    TimeSpan.FromMilliseconds(DesignTokens.Animation.ExpandedToHoverDurationMs));
            }
            else if (previousState == NotchState.ScreenshotHistory)
            {
                StartCollapseKeyFrameAnimations(
                    targetDimensions,
                    TimeSpan.FromMilliseconds(DesignTokens.Screenshot.HistoryExitAnimationDurationMs));
            }
            else if (previousState == NotchState.DropTarget)
            {
                StartCollapseKeyFrameAnimations(
                    targetDimensions,
                    TimeSpan.FromMilliseconds(DesignTokens.Animation.DragLeaveDurationMs));
            }
            else
            {
                StartHoverExpansionAnimations(targetDimensions);
            }
        }
        else
        {
            int collapseDurationMs = previousState switch
            {
                NotchState.DropTarget => DesignTokens.Animation.DragLeaveDurationMs,
                NotchState.ScreenshotHistory => DesignTokens.Screenshot.HistoryExitAnimationDurationMs,
                _ => DesignTokens.Animation.HoverToIdleDurationMs
            };

            StartCollapseKeyFrameAnimations(
                targetDimensions,
                TimeSpan.FromMilliseconds(collapseDurationMs));
        }

        if (batch is not null)
        {
            batch.End();
            batch.Completed += (sender, args) =>
            {
                if (_isDisposed || transitionVersion != _activeTransitionVersion)
                {
                    return;
                }

                onCompleted?.Invoke();
            };
        }
    }

    private void StartHoverExpansionAnimations(NotchDimensions target)
    {
        TimeSpan springPeriod = TimeSpan.FromMilliseconds(DesignTokens.Animation.HoverSpringPeriodMs);
        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Animation.IdleToHoverDurationMs);
        float dampingRatio = DesignTokens.Animation.HoverSpringDampingRatio;

        StartSpringGeometryAnimations(target, dampingRatio, springPeriod);
        AnimateSurfaceAndDepthVisuals(target, duration);
    }

    private void StartDropTargetExpansionAnimations(NotchDimensions target)
    {
        TimeSpan springPeriod = TimeSpan.FromMilliseconds(DesignTokens.Animation.HoverSpringPeriodMs);
        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Animation.DragEnterDurationMs);
        float dampingRatio = DesignTokens.Animation.HoverSpringDampingRatio;

        StartSpringGeometryAnimations(target, dampingRatio, springPeriod);
        AnimateSurfaceAndDepthVisuals(target, duration);
    }

    private void StartExpandedAnimations(NotchDimensions target)
    {
        TimeSpan springPeriod = TimeSpan.FromMilliseconds(DesignTokens.Animation.ExpandedSpringPeriodMs);
        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Animation.HoverToExpandedDurationMs);
        float dampingRatio = DesignTokens.Animation.ExpandedSpringDampingRatio;

        StartSpringGeometryAnimations(target, dampingRatio, springPeriod);
        AnimateSurfaceAndDepthVisuals(target, duration);
    }

    private void StartScreenshotHistoryExpansionAnimations(NotchDimensions target)
    {
        TimeSpan springPeriod = TimeSpan.FromMilliseconds(DesignTokens.Animation.ExpandedSpringPeriodMs);
        TimeSpan duration = TimeSpan.FromMilliseconds(DesignTokens.Screenshot.HistoryAnimationDurationMs);
        float dampingRatio = DesignTokens.Animation.ExpandedSpringDampingRatio;

        StartSpringGeometryAnimations(target, dampingRatio, springPeriod);
        AnimateSurfaceAndDepthVisuals(target, duration);
    }

    private void StartSpringGeometryAnimations(
        NotchDimensions target,
        float dampingRatio,
        TimeSpan springPeriod)
    {
        SpringVector2NaturalMotionAnimation sizeSpring = _compositor.CreateSpringVector2Animation();
        sizeSpring.FinalValue = new Vector2((float)target.LogicalWidth, (float)target.LogicalHeight);
        sizeSpring.DampingRatio = dampingRatio;
        sizeSpring.Period = springPeriod;

        SpringVector2NaturalMotionAnimation radiusSpring = _compositor.CreateSpringVector2Animation();
        radiusSpring.FinalValue = new Vector2((float)target.CornerRadius, (float)target.CornerRadius);
        radiusSpring.DampingRatio = dampingRatio;
        radiusSpring.Period = springPeriod;

        SpringVector3NaturalMotionAnimation scaleSpring = _compositor.CreateSpringVector3Animation();
        scaleSpring.FinalValue = new Vector3(target.ContentScale, target.ContentScale, 1.0f);
        scaleSpring.DampingRatio = dampingRatio;
        scaleSpring.Period = springPeriod;

        _pillGeometry.StartAnimation("Size", sizeSpring);
        _pillGeometry.StartAnimation("CornerRadius", radiusSpring);
        _contentVisual.StartAnimation("Scale", scaleSpring);
    }

    private void StartCollapseKeyFrameAnimations(NotchDimensions target, TimeSpan collapseDuration)
    {
        Vector2KeyFrameAnimation sizeAnimation = _compositor.CreateVector2KeyFrameAnimation();
        sizeAnimation.Duration = collapseDuration;
        sizeAnimation.InsertKeyFrame(
            1.0f,
            new Vector2((float)target.LogicalWidth, (float)target.LogicalHeight),
            _collapseEasing);

        Vector2KeyFrameAnimation radiusAnimation = _compositor.CreateVector2KeyFrameAnimation();
        radiusAnimation.Duration = collapseDuration;
        radiusAnimation.InsertKeyFrame(
            1.0f,
            new Vector2((float)target.CornerRadius, (float)target.CornerRadius),
            _collapseEasing);

        Vector3KeyFrameAnimation scaleAnimation = _compositor.CreateVector3KeyFrameAnimation();
        scaleAnimation.Duration = collapseDuration;
        scaleAnimation.InsertKeyFrame(
            1.0f,
            new Vector3(target.ContentScale, target.ContentScale, 1.0f),
            _collapseEasing);

        _pillGeometry.StartAnimation("Size", sizeAnimation);
        _pillGeometry.StartAnimation("CornerRadius", radiusAnimation);
        _contentVisual.StartAnimation("Scale", scaleAnimation);

        AnimateSurfaceAndDepthVisuals(target, collapseDuration);
    }

    private void AnimateSurfaceAndDepthVisuals(NotchDimensions target, TimeSpan duration)
    {
        ScalarKeyFrameAnimation contentOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        contentOpacityAnim.Duration = duration;
        contentOpacityAnim.InsertKeyFrame(1.0f, target.ContentOpacity, _collapseEasing);

        ScalarKeyFrameAnimation surfaceOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        surfaceOpacityAnim.Duration = duration;
        surfaceOpacityAnim.InsertKeyFrame(1.0f, ResolveEffectiveSurfaceOpacity(target.SurfaceOpacity), _collapseEasing);

        ScalarKeyFrameAnimation innerDepthOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        innerDepthOpacityAnim.Duration = duration;
        innerDepthOpacityAnim.InsertKeyFrame(1.0f, target.InnerDepthOpacity, _collapseEasing);

        ScalarKeyFrameAnimation borderOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        borderOpacityAnim.Duration = duration;
        borderOpacityAnim.InsertKeyFrame(1.0f, target.BorderOpacity, _collapseEasing);

        ScalarKeyFrameAnimation shadowBlurAnim = _compositor.CreateScalarKeyFrameAnimation();
        shadowBlurAnim.Duration = duration;
        shadowBlurAnim.InsertKeyFrame(1.0f, target.ShadowBlurRadius, _collapseEasing);

        ScalarKeyFrameAnimation shadowOpacityAnim = _compositor.CreateScalarKeyFrameAnimation();
        shadowOpacityAnim.Duration = duration;
        shadowOpacityAnim.InsertKeyFrame(1.0f, target.ShadowOpacity, _collapseEasing);

        Vector3KeyFrameAnimation shadowOffsetAnim = _compositor.CreateVector3KeyFrameAnimation();
        shadowOffsetAnim.Duration = duration;
        shadowOffsetAnim.InsertKeyFrame(1.0f, new Vector3(0.0f, target.ShadowOffsetY, 0.0f), _collapseEasing);

        _contentVisual.StartAnimation("Opacity", contentOpacityAnim);
        _surfaceBaseVisual.StartAnimation("Opacity", surfaceOpacityAnim);
        _innerDepthVisual.StartAnimation("Opacity", innerDepthOpacityAnim);
        _borderShapeVisual.StartAnimation("Opacity", borderOpacityAnim);
        _dropShadow.StartAnimation("BlurRadius", shadowBlurAnim);
        _dropShadow.StartAnimation("Opacity", shadowOpacityAnim);
        _dropShadow.StartAnimation("Offset", shadowOffsetAnim);
    }

    private (SpriteVisual? Visual, CompositionBrush? Brush, bool IsSupported) TryCreateGlassBackdrop()
    {
        // Note: Compositor.CreateBackdropBrush() without a Win2D GaussianBlurEffect (which requires an external
        // Win2D dependency) only passes through raw unblurred pixels behind the window, washing out the dark
        // obsidian Notch surface. Returning (null, null, false) activates the rich deep-obsidian gradient
        // NotchSurfaceGlassBrush at 1.0 opacity with the inner depth sheen and 1px specular border.
        return (null, null, false);
    }

    private float ResolveEffectiveSurfaceOpacity(float requestedOpacity)
    {
        // When backdrop blur is unavailable, fall back to a fully solid dark surface (opacity 1.0).
        return _hasGlassBackdrop ? requestedOpacity : 1.0f;
    }

    private CompositionLinearGradientBrush CreateBorderHighlightBrush()
    {
        CompositionLinearGradientBrush gradientBrush = _compositor.CreateLinearGradientBrush();
        gradientBrush.MappingMode = CompositionMappingMode.Relative;
        gradientBrush.StartPoint = new Vector2(0.5f, 0.0f);
        gradientBrush.EndPoint = new Vector2(0.5f, 1.0f);

        Color topColor = Color.FromArgb(
            DesignTokens.Surface.BorderTopHighlightAlpha,
            DesignTokens.Colors.Primary.R,
            DesignTokens.Colors.Primary.G,
            DesignTokens.Colors.Primary.B);

        Color bottomColor = Color.FromArgb(
            DesignTokens.Surface.BorderBottomHighlightAlpha,
            DesignTokens.Colors.Secondary.R,
            DesignTokens.Colors.Secondary.G,
            DesignTokens.Colors.Secondary.B);

        CompositionColorGradientStop topStop = _compositor.CreateColorGradientStop(0.0f, topColor);
        CompositionColorGradientStop bottomStop = _compositor.CreateColorGradientStop(1.0f, bottomColor);

        gradientBrush.ColorStops.Add(topStop);
        gradientBrush.ColorStops.Add(bottomStop);
        return gradientBrush;
    }

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateContentCenterPoint();
    }

    private void OnSubContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSubContentCenterPoints();
    }

    private void OnSeekTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        float trackWidth = (float)Math.Max(0.0, e.NewSize.Width);
        _seekProgressState.InsertScalar("TrackWidth", trackWidth);
    }

    private void UpdateContentCenterPoint()
    {
        float centerX = (float)(_layers.ContentHost.ActualWidth * 0.5);
        float centerY = (float)(_currentTargetPillHeight * 0.5);
        _contentVisual.CenterPoint = new Vector3(centerX, centerY, 0.0f);
    }

    private void UpdateSubContentCenterPoints()
    {
        _clockViewVisual.CenterPoint = new Vector3(
            (float)(_layers.ClockViewHost.ActualWidth * 0.5),
            (float)(_layers.ClockViewHost.ActualHeight * 0.5),
            0.0f);

        _collapsedScreenshotIndicatorVisual.CenterPoint = new Vector3(
            (float)(_layers.CollapsedScreenshotIndicator.ActualWidth * 0.5),
            (float)(_layers.CollapsedScreenshotIndicator.ActualHeight * 0.5),
            0.0f);

        _clockHoverScreenshotActionVisual.CenterPoint = new Vector3(
            (float)(_layers.ClockHoverScreenshotActionHost.ActualWidth * 0.5),
            (float)(_layers.ClockHoverScreenshotActionHost.ActualHeight * 0.5),
            0.0f);

        _mediaViewVisual.CenterPoint = new Vector3(
            (float)(_layers.MediaViewHost.ActualWidth * 0.5),
            (float)(_layers.MediaViewHost.ActualHeight * 0.5),
            0.0f);

        _mediaControlsVisual.CenterPoint = new Vector3(
            (float)(_layers.MediaControlsHost.ActualWidth * 0.5),
            (float)(_layers.MediaControlsHost.ActualHeight * 0.5),
            0.0f);

        _mediaTimelineVisual.CenterPoint = new Vector3(
            (float)(_layers.MediaTimelineRow.ActualWidth * 0.5),
            (float)(_layers.MediaTimelineRow.ActualHeight * 0.5),
            0.0f);

        _clipboardViewVisual.CenterPoint = new Vector3(
            (float)(_layers.ClipboardViewHost.ActualWidth * 0.5),
            (float)(_layers.ClipboardViewHost.ActualHeight * 0.5),
            0.0f);

        _screenshotViewVisual.CenterPoint = new Vector3(
            (float)(_layers.ScreenshotViewHost.ActualWidth * 0.5),
            (float)(_layers.ScreenshotViewHost.ActualHeight * 0.5),
            0.0f);

        _screenshotHistoryViewVisual.CenterPoint = new Vector3(
            (float)(_layers.ScreenshotHistoryViewHost.ActualWidth * 0.5),
            (float)(_layers.ScreenshotHistoryViewHost.ActualHeight * 0.5),
            0.0f);

        _dropTargetViewVisual.CenterPoint = new Vector3(
            (float)(_layers.DropTargetViewHost.ActualWidth * 0.5),
            (float)(_layers.DropTargetViewHost.ActualHeight * 0.5),
            0.0f);

        _dropTargetPromptVisual.CenterPoint = new Vector3(
            (float)(_layers.DropTargetPromptPanel.ActualWidth * 0.5),
            (float)(_layers.DropTargetPromptPanel.ActualHeight * 0.5),
            0.0f);

        _dropPreviewContentVisual.CenterPoint = new Vector3(
            (float)(_layers.DropPreviewContentPanel.ActualWidth * 0.5),
            (float)(_layers.DropPreviewContentPanel.ActualHeight * 0.5),
            0.0f);

        _calendarViewVisual.CenterPoint = new Vector3(
            (float)(_layers.CalendarViewHost.ActualWidth * 0.5),
            (float)(_layers.CalendarViewHost.ActualHeight * 0.5),
            0.0f);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _activeTransitionVersion++;
        _layers.ContentHost.SizeChanged -= OnContentSizeChanged;
        _layers.ClockViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.ClockHoverScreenshotActionHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.MediaViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.MediaControlsHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.MediaTimelineRow.SizeChanged -= OnSubContentSizeChanged;
        _layers.ClipboardViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.ScreenshotViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.ScreenshotHistoryViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.DropTargetViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.DropTargetPromptPanel.SizeChanged -= OnSubContentSizeChanged;
        _layers.DropPreviewContentPanel.SizeChanged -= OnSubContentSizeChanged;
        _layers.CalendarViewHost.SizeChanged -= OnSubContentSizeChanged;
        _layers.SeekTrackBackground.SizeChanged -= OnSeekTrackSizeChanged;

        _pillGeometry.StopAnimation("Offset");
        _pillGeometry.StopAnimation("Size");
        _pillGeometry.StopAnimation("CornerRadius");

        _shadowVisual.StopAnimation("Size");
        _shadowVisual.StopAnimation("Offset");
        _dropShadow.StopAnimation("BlurRadius");
        _dropShadow.StopAnimation("Opacity");
        _dropShadow.StopAnimation("Offset");

        _borderGeometry.StopAnimation("Size");
        _borderGeometry.StopAnimation("Offset");
        _borderGeometry.StopAnimation("CornerRadius");
        _borderShapeVisual.StopAnimation("Opacity");

        _surfaceBaseVisual.StopAnimation("Opacity");
        _innerDepthVisual.StopAnimation("Opacity");

        _contentVisual.StopAnimation("Translation");
        _contentVisual.StopAnimation("Scale");
        _contentVisual.StopAnimation("Opacity");
        _clockViewVisual.StopAnimation("Translation");
        _clockViewVisual.StopAnimation("Opacity");
        _clockViewVisual.StopAnimation("Scale");
        _clockHoverScreenshotActionVisual.StopAnimation("Opacity");
        _clockHoverScreenshotActionVisual.StopAnimation("Scale");
        _collapsedMediaIndicatorVisual.StopAnimation("Opacity");
        _collapsedClipboardIndicatorVisual.StopAnimation("Opacity");
        _collapsedScreenshotIndicatorVisual.StopAnimation("Opacity");
        _collapsedScreenshotIndicatorVisual.StopAnimation("Scale");
        _collapsedDropIndicatorVisual.StopAnimation("Opacity");
        _collapsedCalendarIndicatorVisual.StopAnimation("Opacity");
        _primaryClockVisual.StopAnimation("Opacity");
        _primaryClockVisual.StopAnimation("Translation");
        _secondaryClockVisual.StopAnimation("Opacity");
        _secondaryClockVisual.StopAnimation("Translation");
        _mediaViewVisual.StopAnimation("Opacity");
        _mediaViewVisual.StopAnimation("Scale");
        _mediaControlsVisual.StopAnimation("Opacity");
        _mediaControlsVisual.StopAnimation("Scale");
        _mediaTimelineVisual.StopAnimation("Opacity");
        _mediaTimelineVisual.StopAnimation("Scale");
        _clipboardViewVisual.StopAnimation("Opacity");
        _clipboardViewVisual.StopAnimation("Scale");
        _screenshotViewVisual.StopAnimation("Opacity");
        _screenshotViewVisual.StopAnimation("Scale");
        _screenshotHistoryViewVisual.StopAnimation("Opacity");
        _screenshotHistoryViewVisual.StopAnimation("Scale");
        _dropTargetViewVisual.StopAnimation("Opacity");
        _dropTargetViewVisual.StopAnimation("Scale");
        _dropTargetPromptVisual.StopAnimation("Opacity");
        _dropTargetPromptVisual.StopAnimation("Scale");
        _dropPreviewContentVisual.StopAnimation("Opacity");
        _dropPreviewContentVisual.StopAnimation("Scale");
        _calendarViewVisual.StopAnimation("Opacity");
        _calendarViewVisual.StopAnimation("Scale");

        _seekProgressState.StopAnimation("Progress");
        _seekFillClipGeometry.StopAnimation("Size");
        _seekThumbVisual.StopAnimation("Translation");
        _seekThumbVisual.StopAnimation("Scale");
        _seekTrackFillVisual.Clip = null;

        ElementCompositionPreview.SetElementChildVisual(_layers.ShadowLayer, null);
        ElementCompositionPreview.SetElementChildVisual(_layers.BackdropLayer, null);
        ElementCompositionPreview.SetElementChildVisual(_layers.BorderLayer, null);

        _seekFillSizeExpression.Dispose();
        _seekThumbTranslationExpression.Dispose();
        _seekFillGeometricClip.Dispose();
        _seekFillClipGeometry.Dispose();
        _seekProgressState.Dispose();
        _linearEasing.Dispose();

        _shadowSizeExpression.Dispose();
        _shadowOffsetExpression.Dispose();
        _dropShadow.Dispose();
        _shadowVisual.Dispose();

        _glassBackdropVisual?.Dispose();
        _glassBackdropBrush?.Dispose();

        _borderSizeExpression.Dispose();
        _borderOffsetExpression.Dispose();
        _borderRadiusExpression.Dispose();
        _borderStrokeBrush.Dispose();
        _borderSpriteShape.Dispose();
        _borderGeometry.Dispose();
        _borderShapeVisual.Dispose();

        _pillOffsetExpression.Dispose();
        _clockTranslationExpression.Dispose();
        _collapseEasing.Dispose();
        _geometricClip.Dispose();
        _pillGeometry.Dispose();
    }
}
