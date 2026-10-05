using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel.Appointments;
using WindowsNotch.Core;

namespace WindowsNotch.Services.Calendar;

/// <summary>
/// Native Windows calendar service backed by <see cref="Windows.ApplicationModel.Appointments"/>.
/// Queries only the single next upcoming non-all-day event and refreshes strictly on store changes,
/// system time/wake events, throttled user interaction, and coarse event-boundary wakeups.
/// </summary>
public sealed class WindowsCalendarService : ICalendarService
{
    private static readonly TimeSpan LookaheadWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan BoundarySafetyMargin = TimeSpan.FromSeconds(1);
    private const uint MaxAppointmentsToInspect = 32;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _scheduleLock = new();

    private AppointmentStore? _store;
    private CancellationTokenSource? _serviceCts;
    private CancellationTokenSource? _boundaryWakeupCts;
    private bool _isRunning;
    private bool _isDisposed;
    private bool _isStoreUnavailable;
    private bool _hasLoggedUnavailable;

    public CalendarState CurrentState { get; private set; } = CalendarState.Empty;

    public event EventHandler<CalendarState>? CalendarStateChanged;

    public void Start()
    {
        if (_isDisposed || _isRunning)
        {
            return;
        }

        _isRunning = true;
        _serviceCts = new CancellationTokenSource();

        SystemEvents.TimeChanged += OnSystemTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _ = RefreshInternalAsync(force: true);
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;

        SystemEvents.TimeChanged -= OnSystemTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        CancelBoundaryWakeup();

        if (_serviceCts is not null)
        {
            try
            {
                _serviceCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _serviceCts.Dispose();
            _serviceCts = null;
        }

        DetachStoreHandler();
    }

    public Task RefreshAsync()
    {
        if (_isDisposed || !_isRunning)
        {
            return Task.CompletedTask;
        }

        return RefreshInternalAsync(force: false);
    }

    private async Task RefreshInternalAsync(bool force)
    {
        if (_isDisposed || !_isRunning || _isStoreUnavailable)
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        if (!force &&
            CurrentState.UpdatedAtUtc is DateTimeOffset lastUpdated &&
            (nowUtc - lastUpdated) < TimeSpan.FromSeconds(DesignTokens.Calendar.InteractionRefreshCooldownSeconds))
        {
            return;
        }

        CancellationToken serviceToken = _serviceCts?.Token ?? CancellationToken.None;
        if (serviceToken.IsCancellationRequested)
        {
            return;
        }

        bool acquired = false;
        try
        {
            acquired = await _refreshGate.WaitAsync(0, serviceToken).ConfigureAwait(false);
            if (!acquired)
            {
                return;
            }

            AppointmentStore? store = await EnsureStoreAsync(serviceToken).ConfigureAwait(false);
            if (store is null || serviceToken.IsCancellationRequested || !_isRunning || _isDisposed)
            {
                return;
            }

            DateTimeOffset queryNow = DateTimeOffset.Now;
            CalendarEvent? nextEvent = await QuerySingleNextEventAsync(store, queryNow, serviceToken).ConfigureAwait(false);
            if (serviceToken.IsCancellationRequested || !_isRunning || _isDisposed)
            {
                return;
            }

            CalendarState nextState = new(
                IsAvailable: true,
                NextEvent: nextEvent,
                UpdatedAtUtc: DateTimeOffset.UtcNow);

            UpdateState(nextState);
            ScheduleNextCoarseWakeup(nextEvent, DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            HandleStoreUnavailable();
        }
        catch (Exception)
        {
            // Keep service resilient without logging sensitive calendar details.
            ScheduleNextCoarseWakeup(CurrentState.NextEvent, DateTimeOffset.Now);
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    _refreshGate.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private async Task<AppointmentStore?> EnsureStoreAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return _store;
        }

        if (_isStoreUnavailable)
        {
            return null;
        }

        try
        {
            AppointmentStore? store = await AppointmentManager
                .RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (store is null)
            {
                HandleStoreUnavailable();
                return null;
            }

            _store = store;

            try
            {
                _store.StoreChanged += OnAppointmentStoreChanged;
            }
            catch
            {
                // StoreChanged may not be supported on all Windows configurations; coarse refresh handles fallback.
            }

            return _store;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            HandleStoreUnavailable();
            return null;
        }
    }

    private static async Task<CalendarEvent?> QuerySingleNextEventAsync(
        AppointmentStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        FindAppointmentsOptions options = new()
        {
            IncludeHidden = false,
            MaxCount = MaxAppointmentsToInspect
        };

        options.FetchProperties.Clear();
        options.FetchProperties.Add(AppointmentProperties.Subject);
        options.FetchProperties.Add(AppointmentProperties.StartTime);
        options.FetchProperties.Add(AppointmentProperties.Duration);
        options.FetchProperties.Add(AppointmentProperties.AllDay);
        options.FetchProperties.Add(AppointmentProperties.CalendarId);
        options.FetchProperties.Add(AppointmentProperties.IsCanceledMeeting);

        IReadOnlyList<Appointment>? appointments = await store
            .FindAppointmentsAsync(now, LookaheadWindow, options)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (appointments is null || appointments.Count == 0)
        {
            return null;
        }

        Appointment? bestAppointment = null;
        DateTimeOffset bestStart = default;
        DateTimeOffset bestEnd = default;

        for (int i = 0; i < appointments.Count; i++)
        {
            Appointment candidate = appointments[i];
            if (candidate is null || candidate.AllDay || candidate.IsCanceledMeeting)
            {
                continue;
            }

            DateTimeOffset candidateStart = candidate.StartTime;
            TimeSpan duration = candidate.Duration > TimeSpan.Zero ? candidate.Duration : TimeSpan.Zero;
            DateTimeOffset candidateEnd = candidateStart + duration;

            // Ignore events that have already ended (or zero-duration events in the past).
            if (duration > TimeSpan.Zero)
            {
                if (candidateEnd <= now)
                {
                    continue;
                }
            }
            else if (candidateStart <= now)
            {
                continue;
            }

            if (bestAppointment is null ||
                candidateStart < bestStart ||
                (candidateStart == bestStart && candidateEnd < bestEnd))
            {
                bestAppointment = candidate;
                bestStart = candidateStart;
                bestEnd = candidateEnd;
            }
        }

        if (bestAppointment is null)
        {
            return null;
        }

        string normalizedTitle = CalendarEvent.NormalizeTitle(bestAppointment.Subject);
        string? normalizedCalendarName = await TryResolveCalendarNameAsync(
            store,
            bestAppointment.CalendarId,
            cancellationToken).ConfigureAwait(false);

        return new CalendarEvent(
            Title: normalizedTitle,
            StartTime: bestStart,
            EndTime: bestEnd,
            IsAllDay: false,
            CalendarName: normalizedCalendarName);
    }

    private static async Task<string?> TryResolveCalendarNameAsync(
        AppointmentStore store,
        string? calendarId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(calendarId))
        {
            return null;
        }

        try
        {
            AppointmentCalendar? calendar = await store
                .GetAppointmentCalendarAsync(calendarId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);

            return CalendarEvent.NormalizeCalendarName(calendar?.DisplayName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void HandleStoreUnavailable()
    {
        _isStoreUnavailable = true;
        CancelBoundaryWakeup();
        DetachStoreHandler();

        if (!_hasLoggedUnavailable)
        {
            _hasLoggedUnavailable = true;
            Debug.WriteLine("Calendar unavailable");
        }

        UpdateState(CalendarState.Empty);
    }

    private void UpdateState(CalendarState nextState)
    {
        CalendarState previous = CurrentState;
        CurrentState = nextState;

        if (previous.IsAvailable == nextState.IsAvailable &&
            Equals(previous.NextEvent, nextState.NextEvent))
        {
            return;
        }

        CalendarStateChanged?.Invoke(this, nextState);
    }

    private void ScheduleNextCoarseWakeup(CalendarEvent? nextEvent, DateTimeOffset now)
    {
        if (_isDisposed || !_isRunning || _isStoreUnavailable)
        {
            return;
        }

        TimeSpan coarseInterval = TimeSpan.FromMinutes(DesignTokens.Calendar.CoarseRefreshIntervalMinutes);
        TimeSpan delay = coarseInterval;

        if (nextEvent is not null)
        {
            if (nextEvent.StartTime > now)
            {
                TimeSpan untilStart = (nextEvent.StartTime - now) + BoundarySafetyMargin;
                if (untilStart > TimeSpan.Zero && untilStart < delay)
                {
                    delay = untilStart;
                }
            }

            if (nextEvent.EndTime > now)
            {
                TimeSpan untilEnd = (nextEvent.EndTime - now) + BoundarySafetyMargin;
                if (untilEnd > TimeSpan.Zero && untilEnd < delay)
                {
                    delay = untilEnd;
                }
            }
        }

        CancellationTokenSource wakeupCts = new();
        lock (_scheduleLock)
        {
            if (_isDisposed || !_isRunning)
            {
                wakeupCts.Dispose();
                return;
            }

            if (_boundaryWakeupCts is not null)
            {
                try
                {
                    _boundaryWakeupCts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }

                _boundaryWakeupCts.Dispose();
            }

            _boundaryWakeupCts = wakeupCts;
        }

        _ = RunSingleBoundaryWakeupAsync(delay, wakeupCts.Token);
    }

    private async Task RunSingleBoundaryWakeupAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || _isDisposed || !_isRunning)
        {
            return;
        }

        await RefreshInternalAsync(force: true).ConfigureAwait(false);
    }

    private void CancelBoundaryWakeup()
    {
        lock (_scheduleLock)
        {
            if (_boundaryWakeupCts is null)
            {
                return;
            }

            try
            {
                _boundaryWakeupCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _boundaryWakeupCts.Dispose();
            _boundaryWakeupCts = null;
        }
    }

    private void DetachStoreHandler()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            _store.StoreChanged -= OnAppointmentStoreChanged;
        }
        catch
        {
        }

        _store = null;
    }

    private void OnAppointmentStoreChanged(AppointmentStore sender, AppointmentStoreChangedEventArgs args)
    {
        if (_isDisposed || !_isRunning)
        {
            return;
        }

        _ = RefreshInternalAsync(force: true);
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isRunning)
        {
            return;
        }

        _ = RefreshInternalAsync(force: true);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (_isDisposed || !_isRunning || e.Mode != PowerModes.Resume)
        {
            return;
        }

        _ = RefreshInternalAsync(force: true);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        Stop();
        _isDisposed = true;
        _refreshGate.Dispose();
    }
}
