using System;
using System.Globalization;

namespace WindowsNotch.Services.Screenshot;

/// <summary>
/// Immutable in-memory snapshot of a single recent screenshot capture.
/// Stores only compact encoded PNG preview bytes, source dimensions, and UTC capture timestamp.
/// Never stores file paths, window titles, source application names, or clipboard data.
/// </summary>
public sealed record RecentScreenshot(
    byte[] ImageData,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc)
{
    public bool HasImageBytes => ImageData is { Length: > 0 };

    public double AspectRatio =>
        Width > 0 && Height > 0
            ? Width / (double)Height
            : (16.0 / 9.0);

    public string FormatCaptureTimeLocal()
    {
        return CapturedAtUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public string FormatAccessibleName()
    {
        return $"Screenshot captured at {FormatCaptureTimeLocal()}";
    }

    public ScreenshotState ToScreenshotState()
    {
        if (!HasImageBytes || Width <= 0 || Height <= 0)
        {
            return ScreenshotState.Empty;
        }

        return new ScreenshotState(
            IsAvailable: true,
            ImageData: ImageData,
            Width: Width,
            Height: Height,
            CapturedAtUtc: CapturedAtUtc);
    }
}
