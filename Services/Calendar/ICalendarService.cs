using System;
using System.Threading.Tasks;

namespace WindowsNotch.Services.Calendar;

/// <summary>
/// UI-independent contract for native Windows upcoming calendar event detection.
/// </summary>
public interface ICalendarService : IDisposable
{
    CalendarState CurrentState { get; }

    event EventHandler<CalendarState>? CalendarStateChanged;

    Task RefreshAsync();

    void Start();

    void Stop();
}
