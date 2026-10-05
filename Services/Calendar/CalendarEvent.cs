using System;
using System.Globalization;
using System.Text;
using WindowsNotch.Core;

namespace WindowsNotch.Services.Calendar;

/// <summary>
/// Immutable, lightweight representation of the single next upcoming calendar event.
/// Stores only the minimum metadata needed for the Notch preview (never body, attendees, links, or notes).
/// </summary>
public sealed record CalendarEvent(
    string Title,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    bool IsAllDay,
    string? CalendarName)
{
    private const string UntitledEventFallback = "Untitled event";

    public TimeSpan Duration =>
        EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;

    /// <summary>
    /// Formats the event start time in the user's local timezone without seconds:
    /// - Today: "14:30"
    /// - Tomorrow: "Tomorrow · 14:30"
    /// - Farther away: "Oct 8 · 14:30"
    /// </summary>
    public string FormatTimeDisplay(DateTimeOffset referenceNow)
    {
        DateTimeOffset localStart = StartTime.ToLocalTime();
        DateTimeOffset localNow = referenceNow.ToLocalTime();

        string timeText = localStart.ToString("HH:mm", CultureInfo.InvariantCulture);
        DateTime startDate = localStart.Date;
        DateTime todayDate = localNow.Date;

        if (startDate == todayDate)
        {
            return timeText;
        }

        if (startDate == todayDate.AddDays(1))
        {
            return $"Tomorrow \u00B7 {timeText}";
        }

        string datePart = localStart.ToString("MMM d", CultureInfo.InvariantCulture);
        return $"{datePart} \u00B7 {timeText}";
    }

    /// <summary>
    /// Formats a compact event duration when <see cref="EndTime"/> is after <see cref="StartTime"/>:
    /// e.g. "35 min", "1h", "1h 30m". Never displays seconds.
    /// </summary>
    public string FormatDurationDisplay()
    {
        if (EndTime <= StartTime)
        {
            return string.Empty;
        }

        int totalMinutes = (int)Math.Round((EndTime - StartTime).TotalMinutes, MidpointRounding.AwayFromZero);
        if (totalMinutes <= 0)
        {
            return string.Empty;
        }

        if (totalMinutes < 60)
        {
            return $"{totalMinutes.ToString(CultureInfo.InvariantCulture)} min";
        }

        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        if (minutes == 0)
        {
            return $"{hours.ToString(CultureInfo.InvariantCulture)}h";
        }

        return $"{hours.ToString(CultureInfo.InvariantCulture)}h {minutes.ToString(CultureInfo.InvariantCulture)}m";
    }

    /// <summary>
    /// Formats the secondary subtitle line for the Calendar Hover preview (e.g. "14:30 · 35 min").
    /// </summary>
    public string FormatHoverSubtitle(DateTimeOffset referenceNow)
    {
        string timePart = FormatTimeDisplay(referenceNow);
        string durationPart = FormatDurationDisplay();

        if (string.IsNullOrEmpty(durationPart))
        {
            return timePart;
        }

        return $"{timePart} \u00B7 {durationPart}";
    }

    /// <summary>
    /// Normalizes an event title by removing line breaks, collapsing repeated whitespace,
    /// trimming, and truncating to <see cref="DesignTokens.Calendar.MaxTitleCharacters"/> (36 characters).
    /// </summary>
    public static string NormalizeTitle(string? rawTitle)
    {
        string normalized = CollapseAndTruncate(rawTitle, DesignTokens.Calendar.MaxTitleCharacters);
        return string.IsNullOrEmpty(normalized) ? UntitledEventFallback : normalized;
    }

    /// <summary>
    /// Normalizes an optional calendar name without retaining unnecessary characters.
    /// </summary>
    public static string? NormalizeCalendarName(string? rawCalendarName)
    {
        string normalized = CollapseAndTruncate(rawCalendarName, DesignTokens.Calendar.MaxTitleCharacters);
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static string CollapseAndTruncate(string? input, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(input) || maxChars <= 0)
        {
            return string.Empty;
        }

        int capacity = Math.Min(input.Length, maxChars);
        StringBuilder builder = new(capacity);
        bool previousWasWhitespace = false;

        foreach (char c in input)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (builder.Length > 0 && !previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            if (builder.Length >= maxChars)
            {
                break;
            }

            builder.Append(c);
            previousWasWhitespace = false;
        }

        while (builder.Length > 0 && builder[^1] == ' ')
        {
            builder.Length--;
        }

        return builder.ToString();
    }
}
