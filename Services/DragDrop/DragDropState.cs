using System;
using System.Globalization;
using System.Text;
using WindowsNotch.Core;

namespace WindowsNotch.Services.DragDrop;

/// <summary>
/// Extension-based visual icon classification for dropped files.
/// Used strictly for UI icon selection without inspecting or reading file contents.
/// </summary>
public enum DroppedFileCategory
{
    Generic = 0,
    Image,
    Document,
    Archive,
    Code
}

/// <summary>
/// Immutable representation of the current drag-and-drop interaction or the latest dropped file preview.
/// Retains only lightweight metadata in memory (O(1) memory relative to file size) with no history or file content reads.
/// </summary>
public sealed record DragDropState(
    bool IsDragging,
    bool HasDropPreview,
    string? FileName,
    string? Extension,
    string? FilePath,
    long FileSizeBytes,
    bool IsSupported)
{
    public static DragDropState Empty =>
        new(
            IsDragging: false,
            HasDropPreview: false,
            FileName: null,
            Extension: null,
            FilePath: null,
            FileSizeBytes: 0,
            IsSupported: false);

    public bool IsTooLarge =>
        FileSizeBytes > DesignTokens.DragDrop.MaxFileSizeBytes;

    public DroppedFileCategory Category =>
        ClassifyExtension(Extension);

    public string FormattedFileSize =>
        FormatFileSize(FileSizeBytes);

    /// <summary>
    /// Normalizes a raw filename into a single-line trimmed string capped at <paramref name="maxCharacters"/> with an ellipsis.
    /// </summary>
    public static string NormalizeFileName(string? rawFileName, int maxCharacters = DesignTokens.DragDrop.MaxFileNameCharacters)
    {
        if (string.IsNullOrWhiteSpace(rawFileName) || maxCharacters <= 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = rawFileName.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder sb = new(Math.Min(span.Length, maxCharacters + 1));
        bool previousWasWhitespace = false;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!previousWasWhitespace && sb.Length > 0)
                {
                    sb.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            previousWasWhitespace = false;
            sb.Append(c);

            if (sb.Length > maxCharacters)
            {
                break;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        if (sb.Length > maxCharacters)
        {
            int cutLength = Math.Max(1, maxCharacters - 1);
            while (cutLength > 1 && sb[cutLength - 1] == ' ')
            {
                cutLength--;
            }

            sb.Length = cutLength;
            sb.Append('\u2026');
        }

        return sb.ToString();
    }

    public static DroppedFileCategory ClassifyExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return DroppedFileCategory.Generic;
        }

        string normalized = extension.Trim();
        if (!normalized.StartsWith('.'))
        {
            normalized = "." + normalized;
        }

        return normalized.ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => DroppedFileCategory.Image,
            ".pdf" or ".doc" or ".docx" or ".txt" or ".md" => DroppedFileCategory.Document,
            ".zip" or ".rar" or ".7z" => DroppedFileCategory.Archive,
            ".cs" or ".cpp" or ".h" or ".ts" or ".tsx" or ".js" or ".json" or ".xml" or ".yaml" or ".yml" => DroppedFileCategory.Code,
            _ => DroppedFileCategory.Generic
        };
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        const double OneKilobyte = 1024.0;
        const double OneMegabyte = 1024.0 * 1024.0;
        const double OneGigabyte = 1024.0 * 1024.0 * 1024.0;

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        if (bytes < 1024 * 1024)
        {
            double kb = bytes / OneKilobyte;
            return string.Create(CultureInfo.InvariantCulture, $"{kb:0.#} KB");
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            double mb = bytes / OneMegabyte;
            return string.Create(CultureInfo.InvariantCulture, $"{mb:0.0} MB");
        }

        double gb = bytes / OneGigabyte;
        return string.Create(CultureInfo.InvariantCulture, $"{gb:0.00} GB");
    }
}
