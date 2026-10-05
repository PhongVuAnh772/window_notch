using System;

namespace WindowsNotch.Services.Screenshot;

/// <summary>
/// Immutable in-memory snapshot of the latest primary-display screenshot preview.
/// Holds only the latest compact encoded PNG preview bytes in memory with zero history and zero persistence.
/// </summary>
public sealed record ScreenshotState(
    bool IsAvailable,
    byte[]? ImageData,
    int Width,
    int Height,
    DateTimeOffset? CapturedAtUtc)
{
    public static ScreenshotState Empty { get; } = new(
        IsAvailable: false,
        ImageData: null,
        Width: 0,
        Height: 0,
        CapturedAtUtc: null);

    public bool HasImageBytes => ImageData is { Length: > 0 };

    public double AspectRatio =>
        Width > 0 && Height > 0
            ? Width / (double)Height
            : (16.0 / 9.0);

    /// <summary>
    /// Computes aspect-ratio-preserving preview dimensions within (<paramref name="maxWidth"/>, <paramref name="maxHeight"/>)
    /// without upscaling small source dimensions.
    /// </summary>
    public static (int PreviewWidth, int PreviewHeight) ComputePreviewDimensions(
        int sourceWidth,
        int sourceHeight,
        int maxWidth,
        int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || maxWidth <= 0 || maxHeight <= 0)
        {
            return (0, 0);
        }

        double widthRatio = maxWidth / (double)sourceWidth;
        double heightRatio = maxHeight / (double)sourceHeight;
        double scale = Math.Min(1.0, Math.Min(widthRatio, heightRatio));

        int previewWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int previewHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (previewWidth, previewHeight);
    }
}
