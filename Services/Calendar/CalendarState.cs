using System;

namespace WindowsNotch.Services.Calendar;

/// <summary>
/// Immutable snapshot of the single next upcoming calendar event for the Notch.
/// Retains at most one upcoming event in memory and never stores event history or collections.
/// </summary>
public sealed record CalendarState(
    bool IsAvailable,
    CalendarEvent? NextEvent,
    DateTimeOffset? UpdatedAtUtc)
{
    public static CalendarState Empty =>
        new(false, null, null);

    public bool HasUpcomingEvent =>
        IsAvailable && NextEvent is not null;
}
