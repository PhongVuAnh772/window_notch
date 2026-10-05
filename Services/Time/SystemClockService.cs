using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WindowsNotch.Services.Time;

/// <summary>
/// Event-driven system clock service that schedules wakeups strictly at the next minute boundary.
/// Avoids polling loops and high-frequency timers.
/// </summary>
public sealed class SystemClockService : IClockService
{
    private const string TimeFormat = "HH:mm";
    private const int BoundarySafetyMarginMs = 20;

    private readonly Func<DateTime> _nowProvider;
    private CancellationTokenSource? _scheduleCts;
    private bool _isRunning;
    private bool _isDisposed;

    public SystemClockService()
        : this( static () => DateTime.Now)
    {
    }

    public SystemClockService(Func<DateTime> nowProvider)
    {
        ArgumentNullException.ThrowIfNull(nowProvider);
        _nowProvider = nowProvider;
        CurrentTimeText = FormatTime(_nowProvider());
    }

    public string CurrentTimeText { get; private set; }

    public event EventHandler<string>? TimeChanged;

    public void Start()
    {
        if (_isDisposed || _isRunning)
        {
            return;
        }

        _isRunning = true;
        UpdateTimeIfChanged();

        SystemEvents.TimeChanged += OnSystemTimeChanged;
        StartMinuteBoundarySchedule();
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        CancelSchedule();
    }

    public static TimeSpan CalculateDelayUntilNextMinute(DateTime now)
    {
        int remainingSeconds = 59 - now.Second;
        int remainingMilliseconds = 1000 - now.Millisecond + BoundarySafetyMarginMs;
        return TimeSpan.FromMilliseconds((remainingSeconds * 1000) + remainingMilliseconds);
    }

    public static string FormatTime(DateTime time)
    {
        return time.ToString(TimeFormat, CultureInfo.InvariantCulture);
    }

    private void StartMinuteBoundarySchedule()
    {
        CancelSchedule();

        CancellationTokenSource cts = new();
        _scheduleCts = cts;
        _ = RunMinuteBoundaryLoopAsync(cts.Token);
    }

    private async Task RunMinuteBoundaryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_isDisposed)
        {
            TimeSpan delay = CalculateDelayUntilNextMinute(_nowProvider());

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested || _isDisposed)
            {
                return;
            }

            UpdateTimeIfChanged();
        }
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isRunning)
        {
            return;
        }

        UpdateTimeIfChanged();
        StartMinuteBoundarySchedule();
    }

    private void UpdateTimeIfChanged()
    {
        string formatted = FormatTime(_nowProvider());
        if (string.Equals(CurrentTimeText, formatted, StringComparison.Ordinal))
        {
            return;
        }

        CurrentTimeText = formatted;
        TimeChanged?.Invoke(this, formatted);
    }

    private void CancelSchedule()
    {
        if (_scheduleCts is null)
        {
            return;
        }

        _scheduleCts.Cancel();
        _scheduleCts.Dispose();
        _scheduleCts = null;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Stop();
        _isDisposed = true;
    }
}
