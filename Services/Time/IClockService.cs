using System;

namespace WindowsNotch.Services.Time;

/// <summary>
/// Provides formatted local time ("HH:mm") and notifies subscribers at each minute boundary.
/// </summary>
public interface IClockService : IDisposable
{
    string CurrentTimeText { get; }

    event EventHandler<string>? TimeChanged;

    void Start();

    void Stop();
}
