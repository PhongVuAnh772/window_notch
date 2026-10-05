using System;

namespace WindowsNotch.Services.Clipboard;

/// <summary>
/// Categorizes the current system clipboard payload for lightweight Notch presentation.
/// </summary>
public enum ClipboardContentType
{
    None = 0,
    Text = 1,
    Image = 2,
    Unsupported = 3
}

/// <summary>
/// Immutable UI-independent snapshot of the latest system clipboard item.
/// Retains only normalized preview metadata in memory with zero persistence.
/// </summary>
public sealed record ClipboardState(
    bool IsAvailable,
    ClipboardContentType ContentType,
    string PreviewText,
    byte[]? ImageData,
    DateTimeOffset UpdatedAt)
{
    public static ClipboardState None { get; } = new(
        IsAvailable: false,
        ContentType: ClipboardContentType.None,
        PreviewText: string.Empty,
        ImageData: null,
        UpdatedAt: default);

    public bool HasImageBytes => ImageData is { Length: > 0 };

    public bool HasSameContentAs(ClipboardState? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        return IsAvailable == other.IsAvailable
            && ContentType == other.ContentType
            && string.Equals(PreviewText, other.PreviewText, StringComparison.Ordinal)
            && AreBytesEqual(ImageData, other.ImageData);
    }

    private static bool AreBytesEqual(byte[]? left, byte[]? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }
}
