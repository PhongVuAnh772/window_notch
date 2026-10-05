using System;

namespace WindowsNotch.Services.Media;

/// <summary>
/// UI-independent playback status mirroring Windows SMTC session states.
/// </summary>
public enum MediaPlaybackStatus
{
    Closed = 0,
    Opened = 1,
    Changing = 2,
    Stopped = 3,
    Playing = 4,
    Paused = 5
}

/// <summary>
/// Immutable UI-independent snapshot of the media session timeline and seek bounds.
/// </summary>
public sealed record MediaTimeline(
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan Position,
    TimeSpan MinSeekTime,
    TimeSpan MaxSeekTime,
    DateTimeOffset LastUpdatedTime,
    bool IsSeekSupported)
{
    public TimeSpan Duration => EndTime > StartTime ? EndTime - StartTime : TimeSpan.Zero;

    public bool HasValidTimeline => EndTime > StartTime && Duration > TimeSpan.Zero;

    public bool CanSeek => IsSeekSupported && HasValidTimeline && MaxSeekTime > MinSeekTime;

    /// <summary>
    /// Computes the elapsed time relative to <see cref="StartTime"/>, optionally interpolating
    /// from <see cref="LastUpdatedTime"/> when playback is active.
    /// </summary>
    public TimeSpan GetInterpolatedElapsed(bool isPlaying, DateTimeOffset nowUtc)
    {
        if (!HasValidTimeline)
        {
            return TimeSpan.Zero;
        }

        TimeSpan absolutePosition = Position;

        if (isPlaying && LastUpdatedTime != default && nowUtc > LastUpdatedTime)
        {
            TimeSpan delta = nowUtc - LastUpdatedTime;
            if (delta > TimeSpan.Zero && delta <= Duration)
            {
                absolutePosition += delta;
            }
        }

        TimeSpan clampedAbsolute = ClampTimeSpan(absolutePosition, StartTime, EndTime);
        TimeSpan elapsed = clampedAbsolute - StartTime;
        return ClampTimeSpan(elapsed, TimeSpan.Zero, Duration);
    }

    /// <summary>
    /// Maps a normalized [0.0, 1.0] progress ratio across the timeline duration
    /// and clamps the result to the valid seek bounds [<see cref="MinSeekTime"/>, <see cref="MaxSeekTime"/>].
    /// </summary>
    public TimeSpan ResolveSeekPosition(double normalizedRatio)
    {
        if (!HasValidTimeline)
        {
            return TimeSpan.Zero;
        }

        double clampedRatio = Math.Clamp(normalizedRatio, 0.0, 1.0);
        long offsetTicks = (long)Math.Round(Duration.Ticks * clampedRatio);
        TimeSpan candidate = StartTime + TimeSpan.FromTicks(offsetTicks);

        TimeSpan lowerBound = MinSeekTime > StartTime ? MinSeekTime : StartTime;
        TimeSpan upperBound = MaxSeekTime < EndTime && MaxSeekTime > lowerBound ? MaxSeekTime : EndTime;

        return ClampTimeSpan(candidate, lowerBound, upperBound);
    }

    private static TimeSpan ClampTimeSpan(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }
}

/// <summary>
/// Immutable snapshot of the system media session state.
/// </summary>
public sealed record MediaState(
    bool IsAvailable,
    bool IsPlaying,
    MediaPlaybackStatus PlaybackStatus,
    MediaTrack? Track,
    TimeSpan? Position,
    TimeSpan? Duration,
    MediaTimeline? Timeline = null)
{
    public static MediaState Inactive { get; } = new(
        IsAvailable: false,
        IsPlaying: false,
        PlaybackStatus: MediaPlaybackStatus.Closed,
        Track: null,
        Position: null,
        Duration: null,
        Timeline: null);
}
