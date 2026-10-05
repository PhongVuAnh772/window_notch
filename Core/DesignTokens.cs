using Windows.UI;

namespace WindowsNotch.Core;

/// <summary>
/// Design system tokens strictly reflecting .antigravity/design.md.
/// </summary>
public static class DesignTokens
{
    public static class Typography
    {
        public const string FontFamily = "Segoe UI Variable";

        public const double CaptionSize = 11.0;
        public const double SecondarySize = 12.0;
        public const double BodySize = 13.0;
        public const double TitleSize = 14.0;
        public const double LargeSize = 20.0;
        public const double HeroSize = 24.0;

        public const ushort WeightRegular = 400;
        public const ushort WeightMedium = 500;
        public const ushort WeightSemibold = 600;
    }

    public static class Colors
    {
        public static readonly Color Background = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        public static readonly Color Surface = Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0C);
        public static readonly Color Elevated = Color.FromArgb(0xFF, 0x18, 0x18, 0x1B);
        public static readonly Color Primary = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Color Secondary = Color.FromArgb(0xFF, 0xA1, 0xA1, 0xAA);
        public static readonly Color Tertiary = Color.FromArgb(0xFF, 0x71, 0x71, 0x7A);
        public static readonly Color Accent = Color.FromArgb(0xFF, 0x8B, 0x8B, 0xFF);
    }

    public static class Spacing
    {
        public const double S4 = 4.0;
        public const double S8 = 8.0;
        public const double S12 = 12.0;
        public const double S16 = 16.0;
        public const double S20 = 20.0;
        public const double S24 = 24.0;
        public const double S32 = 32.0;
    }

    public static class Radius
    {
        public const double R8 = 8.0;
        public const double R12 = 12.0;
        public const double R16 = 16.0;
        public const double R20 = 20.0;
        public const double R24 = 24.0;

        public static double Pill(double height) => height / 2.0;
    }

    public static class NotchSize
    {
        public const double CollapsedWidth = 140.0;
        public const double CollapsedHeight = 30.0;

        public const double HoverWidth = 220.0;
        public const double HoverHeight = 44.0;

        public const double DropTargetWidth = 300.0;
        public const double DropTargetHeight = 64.0;

        public const double MediumWidth = 320.0;
        public const double MediumHeight = 120.0;

        public const double MediaExpandedWidth = 380.0;
        public const double MediaExpandedHeight = 88.0;

        public const double ExpandedWidth = 420.0;
        public const double ExpandedHeight = 260.0;
    }

    public static class Animation
    {
        public const int IdleToHoverDurationMs = 240;
        public const int HoverToIdleDurationMs = 220;
        public const int HoverToExpandedDurationMs = 280;
        public const int ExpandedToHoverDurationMs = 240;
        public const int MouseLeaveGraceDelayMs = 350;

        public const int DragEnterDurationMs = 200;
        public const int DropSuccessDurationMs = 180;
        public const int DragLeaveDurationMs = 220;

        public const float HoverSpringDampingRatio = 0.80f;
        public const int HoverSpringPeriodMs = 120;

        public const float ExpandedSpringDampingRatio = 0.82f;
        public const int ExpandedSpringPeriodMs = 140;

        public const float CollapseBezierX1 = 0.16f;
        public const float CollapseBezierY1 = 1.00f;
        public const float CollapseBezierX2 = 0.30f;
        public const float CollapseBezierY2 = 1.00f;

        public const float IdleContentScale = 1.00f;
        public const float HoverContentScale = 1.02f;
        public const float ExpandedContentScale = 1.00f;

        public const float IdleContentOpacity = 0.92f;
        public const float HoverContentOpacity = 1.00f;
        public const float ExpandedContentOpacity = 1.00f;

        public const double SpringOverscanHorizontal = 16.0;
        public const double SpringOverscanVertical = 8.0;

        public const int MinuteChangeDurationMs = 140;
        public const float MinuteChangeSlideOffsetPx = 3.0f;

        public const int ContentSwitchDurationMs = 180;
        public const int ContentSwitchDisappearDurationMs = 140;
        public const float ContentSwitchScale = 0.96f;
    }

    public static class Media
    {
        public const double HoverArtworkSize = 28.0;
        public const double HoverArtworkCornerRadius = Radius.R8;
        public const double HoverLeftPadding = Spacing.S12;
        public const double HoverRightPadding = Spacing.S8;
        public const float CollapsedIndicatorOpacity = 0.85f;

        public const double ExpandedArtworkSize = 40.0;
        public const double ExpandedArtworkCornerRadius = Radius.R8;
        public const double ExpandedHorizontalPadding = Spacing.S16;
        public const double ExpandedVerticalPadding = Spacing.S12;

        public const double ControlButtonSize = Spacing.S24;
        public const double ControlButtonCornerRadius = Radius.R8;
        public const double ControlButtonSpacing = 2.0;
        public const double ControlIconSize = 10.0;

        public const int ControlHoverDurationMs = 120;
        public const float ControlPressedScale = 0.95f;

        public const float ControlIconRestOpacity = 0.78f;
        public const float ControlIconActiveOpacity = 1.00f;

        public const float ControlBackgroundRestOpacity = 0.00f;
        public const float ControlBackgroundHoverOpacity = 0.85f;
        public const float ControlBackgroundPressedOpacity = 1.00f;

        public const double SeekTrackHeight = 3.0;
        public const double SeekTrackCornerRadius = 1.5;
        public const double SeekThumbSize = 8.0;
        public const double SeekThumbCornerRadius = 4.0;
        public const double SeekHitTargetHeight = Spacing.S16;

        public const float SeekTrackInactiveOpacity = 0.38f;
        public const float SeekTrackActiveOpacity = 0.92f;
        public const float SeekThumbRestScale = 1.00f;
        public const float SeekThumbActiveScale = 1.25f;

        public const int CommandCooldownMs = 120;
    }

    public static class Clipboard
    {
        public const int PreviewMaxCharacters = 100;
        public const double HoverPreviewSize = 28.0;
        public const double HoverPreviewCornerRadius = Radius.R8;
        public const double HoverHorizontalPadding = Spacing.S12;
        public const double CollapsedIconSize = 11.0;
        public const double HoverIconSize = 12.0;
        public const float CollapsedIndicatorOpacity = 0.85f;
        public const int ContentTransitionDurationMs = Animation.ContentSwitchDurationMs;
        public const int MaxCompactImageBytes = 512 * 1024;
    }

    public static class Screenshot
    {
        public const int PreviewMaxWidth = 320;
        public const int PreviewMaxHeight = 180;
        public const int MaxEncodedImageBytes = 4 * 1024 * 1024;
        public const int MaxImageBytes = MaxEncodedImageBytes;
        public const int CaptureCommandCooldownMs = 200;
        public const int MaxHistoryCount = 3;

        public const double HoverThumbnailMaxWidth = 44.0;
        public const double HoverThumbnailHeight = 28.0;
        public const double HoverThumbnailCornerRadius = Radius.R8;
        public const double HoverLeftPadding = Spacing.S12;
        public const double HoverRightPadding = Spacing.S8;
        public const double CollapsedIconSize = 11.0;
        public const double ActionIconSize = 12.0;
        public const float CollapsedIndicatorOpacity = 0.85f;
        public const int ContentTransitionDurationMs = Animation.ContentSwitchDurationMs;

        public const double HistoryWidth = 380.0;
        public const double HistoryHeight = 120.0;
        public const double HistoryCornerRadius = Radius.R24;
        public const double HistoryHorizontalPadding = Spacing.S16;
        public const double HistoryVerticalPadding = 10.0;
        public const double HistoryThumbnailWidth = 104.0;
        public const double HistoryThumbnailHeight = 54.0;
        public const double HistoryThumbnailCornerRadius = Radius.R8;
        public const double HistoryGap = Spacing.S12;

        public const int HistoryAnimationDurationMs = 240;
        public const int HistoryExitAnimationDurationMs = 200;
        public const int HistoryThumbnailAppearDurationMs = 160;
        public const int HistorySelectionAnimationDurationMs = 140;
        public const int HistoryDecodePixelWidth = 208;
        public const float HistoryThumbnailAppearScale = 0.96f;
        public const float HistoryItemSelectedOpacity = 1.00f;
        public const float HistoryItemUnselectedOpacity = 0.86f;
        public const float HistoryItemHoverScale = 1.02f;
        public const float HistoryItemPressedScale = 0.96f;
        public const float HistoryThumbnailHoverScale = HistoryItemHoverScale;
        public const float HistoryThumbnailPressedScale = HistoryItemPressedScale;
    }

    public static class DropTarget
    {
        public const double Width = NotchSize.DropTargetWidth;
        public const double Height = NotchSize.DropTargetHeight;
        public const double CornerRadius = Radius.R24;
        public const int MaxFileNameCharacters = 64;
        public const long MaxFileSizeBytes = 50L * 1024L * 1024L;

        public const double BadgeSize = Spacing.S32;
        public const double BadgeCornerRadius = Radius.R8;
        public const double IconSize = 14.0;
        public const double CollapsedIconSize = 11.0;
        public const double HorizontalPadding = Spacing.S16;
        public const double VerticalPadding = Spacing.S12;
        public const float CollapsedIndicatorOpacity = 0.85f;

        public const int DragEnterDurationMs = Animation.DragEnterDurationMs;
        public const int DropSuccessDurationMs = Animation.DropSuccessDurationMs;
        public const int DragLeaveDurationMs = Animation.DragLeaveDurationMs;
    }

    public static class DragDrop
    {
        public const double DropTargetWidth = DropTarget.Width;
        public const double DropTargetHeight = DropTarget.Height;
        public const double DropTargetCornerRadius = DropTarget.CornerRadius;
        public const int MaxFileNameCharacters = DropTarget.MaxFileNameCharacters;
        public const long MaxFileSizeBytes = DropTarget.MaxFileSizeBytes;

        public const double BadgeSize = DropTarget.BadgeSize;
        public const double BadgeCornerRadius = DropTarget.BadgeCornerRadius;
        public const double IconSize = DropTarget.IconSize;
        public const double CollapsedIconSize = DropTarget.CollapsedIconSize;
        public const float CollapsedIndicatorOpacity = DropTarget.CollapsedIndicatorOpacity;

        public const int DragEnterDurationMs = DropTarget.DragEnterDurationMs;
        public const int DropSuccessDurationMs = DropTarget.DropSuccessDurationMs;
        public const int DragLeaveDurationMs = DropTarget.DragLeaveDurationMs;
    }

    public static class Calendar
    {
        public const int MaxTitleCharacters = 36;
        public const double HoverIconSize = Spacing.S24;
        public const double HoverIconCornerRadius = Radius.R8;
        public const double HoverGlyphSize = 12.0;
        public const double CollapsedIconSize = 11.0;
        public const double HoverHorizontalPadding = Spacing.S12;
        public const float CollapsedIndicatorOpacity = 0.85f;
        public const int ContentTransitionDurationMs = Animation.ContentSwitchDurationMs;
        public const int CoarseRefreshIntervalMinutes = 15;
        public const int InteractionRefreshCooldownSeconds = 60;
    }

    public static class Surface
    {
        public const float IdleSurfaceOpacity = 1.0f;
        public const float HoverSurfaceOpacity = 1.0f;
        public const float ExpandedSurfaceOpacity = 1.0f;

        public const float IdleInnerDepthOpacity = 0.40f;
        public const float HoverInnerDepthOpacity = 0.55f;
        public const float ExpandedInnerDepthOpacity = 0.60f;

        public const float BorderStrokeThickness = 1.0f;
        public const float IdleBorderOpacity = 0.12f;
        public const float HoverBorderOpacity = 0.20f;
        public const float ExpandedBorderOpacity = 0.24f;
        public const byte BorderTopHighlightAlpha = 0x66;
        public const byte BorderBottomHighlightAlpha = 0x22;

        public const float IdleShadowBlurRadius = 18.0f;
        public const float HoverShadowBlurRadius = 26.0f;
        public const float ExpandedShadowBlurRadius = 32.0f;

        public const float IdleShadowOpacity = 0.36f;
        public const float HoverShadowOpacity = 0.46f;
        public const float ExpandedShadowOpacity = 0.52f;

        public const float IdleShadowOffsetY = 3.0f;
        public const float HoverShadowOffsetY = 5.0f;
        public const float ExpandedShadowOffsetY = 7.0f;

        public const double ShadowAllowanceHorizontal = 40.0;
        public const double ShadowAllowanceBottom = 30.0;
    }

    public static class Canvas
    {
        public const double HostWidth = 500.0;
        public const double HostHeight = 300.0;
    }
}
