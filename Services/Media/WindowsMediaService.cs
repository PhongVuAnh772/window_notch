using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WindowsNotch.Core;

namespace WindowsNotch.Services.Media;

/// <summary>
/// Native Windows event-driven media service backed by <see cref="GlobalSystemMediaTransportControlsSessionManager"/>.
/// Subscribes to session, metadata, playback, and timeline events without polling timers.
/// </summary>
public sealed class WindowsMediaService : IMediaService
{
    private enum MediaTransportCommandKind
    {
        None = 0,
        Play,
        Pause,
        SkipNext,
        SkipPrevious,
        Seek
    }

    private const int MaxArtworkByteLength = 4 * 1024 * 1024;
    private const string FallbackTrackTitle = "Playing Media";

    private readonly object _syncLock = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _monitoredSessions = new();

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _activeSession;

    private string? _cachedArtworkTrackKey;
    private byte[]? _cachedArtworkBytes;

    private int _isCommandInFlight;
    private MediaTransportCommandKind _lastCommandKind;
    private long _lastCommandTick;

    private int _refreshVersion;
    private bool _isRunning;
    private bool _isInitializing;
    private bool _isDisposed;

    public MediaState CurrentState { get; private set; } = MediaState.Inactive;

    public event EventHandler<MediaState>? MediaStateChanged;

    public void Start()
    {
        lock (_syncLock)
        {
            if (_isDisposed || _isRunning || _isInitializing)
            {
                return;
            }

            _isRunning = true;
            _isInitializing = true;
        }

        _ = InitializeSessionManagerAsync();
    }

    public void Stop()
    {
        lock (_syncLock)
        {
            if (!_isRunning && !_isInitializing)
            {
                return;
            }

            _isRunning = false;
            _isInitializing = false;
            Interlocked.Increment(ref _refreshVersion);

            DetachSessionManagerLocked();
            DetachAllSessionsLocked();

            _cachedArtworkTrackKey = null;
            _cachedArtworkBytes = null;
        }
    }

    public Task<bool> PlayAsync()
    {
        return ExecuteTransportCommandAsync(
            MediaTransportCommandKind.Play,
            static session => session.TryPlayAsync());
    }

    public Task<bool> PauseAsync()
    {
        return ExecuteTransportCommandAsync(
            MediaTransportCommandKind.Pause,
            static session => session.TryPauseAsync());
    }

    public Task<bool> TogglePlayPauseAsync()
    {
        bool isCurrentlyPlaying;
        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning || _activeSession is null)
            {
                return Task.FromResult(false);
            }

            isCurrentlyPlaying = CurrentState.IsPlaying
                || GetSafePlaybackStatus(_activeSession) == MediaPlaybackStatus.Playing;
        }

        return isCurrentlyPlaying ? PauseAsync() : PlayAsync();
    }

    public Task<bool> SkipNextAsync()
    {
        return ExecuteTransportCommandAsync(
            MediaTransportCommandKind.SkipNext,
            static session => session.TrySkipNextAsync());
    }

    public Task<bool> SkipPreviousAsync()
    {
        return ExecuteTransportCommandAsync(
            MediaTransportCommandKind.SkipPrevious,
            static session => session.TrySkipPreviousAsync());
    }

    public Task<bool> SeekAsync(TimeSpan requestedPosition)
    {
        MediaTimeline? timeline;
        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning || _activeSession is null || !CurrentState.IsAvailable)
            {
                return Task.FromResult(false);
            }

            timeline = CurrentState.Timeline;
        }

        if (timeline is null || !timeline.CanSeek)
        {
            return Task.FromResult(false);
        }

        TimeSpan lowerBound = timeline.MinSeekTime > timeline.StartTime ? timeline.MinSeekTime : timeline.StartTime;
        TimeSpan upperBound = timeline.MaxSeekTime < timeline.EndTime && timeline.MaxSeekTime > lowerBound
            ? timeline.MaxSeekTime
            : timeline.EndTime;

        TimeSpan clampedPosition = requestedPosition;
        if (clampedPosition < lowerBound)
        {
            clampedPosition = lowerBound;
        }
        else if (clampedPosition > upperBound)
        {
            clampedPosition = upperBound;
        }

        long requestedTicks = clampedPosition.Ticks;
        if (requestedTicks < 0)
        {
            return Task.FromResult(false);
        }

        return ExecuteTransportCommandAsync(
            MediaTransportCommandKind.Seek,
            session => session.TryChangePlaybackPositionAsync(requestedTicks));
    }

    private async Task<bool> ExecuteTransportCommandAsync(
        MediaTransportCommandKind commandKind,
        Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> operationFactory)
    {
        // Serialize in-flight transport commands so rapid clicks/seeks never flood the WinRT session.
        if (Interlocked.CompareExchange(ref _isCommandInFlight, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            GlobalSystemMediaTransportControlsSession? session;
            long nowTick = Environment.TickCount64;

            lock (_syncLock)
            {
                if (_isDisposed || !_isRunning || _activeSession is null || !CurrentState.IsAvailable)
                {
                    return false;
                }

                // Short cooldown against accidental duplicate rapid clicks of the same command.
                if (commandKind != MediaTransportCommandKind.Seek &&
                    _lastCommandKind == commandKind &&
                    nowTick - _lastCommandTick < DesignTokens.Media.CommandCooldownMs)
                {
                    return false;
                }

                _lastCommandKind = commandKind;
                _lastCommandTick = nowTick;
                session = _activeSession;
            }

            bool succeeded = await operationFactory(session);
            return succeeded && IsRunningSafe();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsMediaService] Transport command '{commandKind}' failed: {ex.Message}");
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _isCommandInFlight, 0);
        }
    }

    private async Task InitializeSessionManagerAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;

        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch
        {
            // Gracefully handle environments where the Windows SMTC service is unavailable.
        }

        bool shouldRefresh = false;

        lock (_syncLock)
        {
            _isInitializing = false;

            if (_isDisposed || !_isRunning || manager is null)
            {
                return;
            }

            _sessionManager = manager;
            _sessionManager.CurrentSessionChanged += OnSessionManagerCurrentSessionChanged;
            _sessionManager.SessionsChanged += OnSessionManagerSessionsChanged;

            RebindSessionsLocked();
            shouldRefresh = true;
        }

        if (shouldRefresh)
        {
            await RefreshActiveSessionAsync(forceArtworkReload: true);
        }
    }

    private void OnSessionManagerCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        HandleSessionTopologyChange(forceArtworkReload: false);
    }

    private void OnSessionManagerSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        HandleSessionTopologyChange(forceArtworkReload: false);
    }

    private void OnMonitoredSessionPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        // A secondary session (e.g., switching between Spotify and YouTube) may transition to Playing.
        // Re-evaluate which session is most relevant and refresh state.
        HandleSessionTopologyChange(forceArtworkReload: false);
    }

    private void OnActiveSessionMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        if (!IsRunningSafe())
        {
            return;
        }

        _ = RefreshActiveSessionAsync(forceArtworkReload: true);
    }

    private void OnActiveSessionTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        if (!IsRunningSafe())
        {
            return;
        }

        _ = RefreshActiveSessionAsync(forceArtworkReload: false);
    }

    private void HandleSessionTopologyChange(bool forceArtworkReload)
    {
        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning || _sessionManager is null)
            {
                return;
            }

            RebindSessionsLocked();
        }

        _ = RefreshActiveSessionAsync(forceArtworkReload);
    }

    private void RebindSessionsLocked()
    {
        if (_sessionManager is null)
        {
            DetachAllSessionsLocked();
            return;
        }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = _sessionManager.GetSessions();
        }
        catch
        {
            sessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();
        }

        // Rebuild monitored session subscriptions for playback switches across multiple apps.
        foreach (GlobalSystemMediaTransportControlsSession oldSession in _monitoredSessions)
        {
            try
            {
                oldSession.PlaybackInfoChanged -= OnMonitoredSessionPlaybackInfoChanged;
            }
            catch
            {
                // Session may have already closed.
            }
        }

        _monitoredSessions.Clear();

        foreach (GlobalSystemMediaTransportControlsSession session in sessions)
        {
            if (session is null)
            {
                continue;
            }

            try
            {
                session.PlaybackInfoChanged += OnMonitoredSessionPlaybackInfoChanged;
                _monitoredSessions.Add(session);
            }
            catch
            {
                // Ignore sessions that disconnected mid-enumeration.
            }
        }

        GlobalSystemMediaTransportControlsSession? resolvedSession = SelectBestSession(_sessionManager, sessions);

        if (ReferenceEquals(_activeSession, resolvedSession))
        {
            return;
        }

        DetachActiveSessionMetadataLocked();
        _activeSession = resolvedSession;

        if (_activeSession is not null)
        {
            try
            {
                _activeSession.MediaPropertiesChanged += OnActiveSessionMediaPropertiesChanged;
                _activeSession.TimelinePropertiesChanged += OnActiveSessionTimelinePropertiesChanged;
            }
            catch
            {
                _activeSession = null;
            }
        }
    }

    private static GlobalSystemMediaTransportControlsSession? SelectBestSession(
        GlobalSystemMediaTransportControlsSessionManager manager,
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> allSessions)
    {
        GlobalSystemMediaTransportControlsSession? currentSession = null;
        try
        {
            currentSession = manager.GetCurrentSession();
        }
        catch
        {
            currentSession = null;
        }

        // 1. If Windows' current session is actively playing, prioritize it immediately.
        if (currentSession is not null && GetSafePlaybackStatus(currentSession) == MediaPlaybackStatus.Playing)
        {
            return currentSession;
        }

        // 2. If another open session is actively playing (e.g., user started YouTube while Spotify was paused),
        // prefer the actively playing session.
        foreach (GlobalSystemMediaTransportControlsSession candidate in allSessions)
        {
            if (candidate is not null && GetSafePlaybackStatus(candidate) == MediaPlaybackStatus.Playing)
            {
                return candidate;
            }
        }

        // 3. Otherwise use Windows' current session if it is Paused/Opened/Changing.
        if (currentSession is not null && IsUsableSessionStatus(GetSafePlaybackStatus(currentSession)))
        {
            return currentSession;
        }

        // 4. Fallback to any remaining paused/open session.
        foreach (GlobalSystemMediaTransportControlsSession candidate in allSessions)
        {
            if (candidate is not null && IsUsableSessionStatus(GetSafePlaybackStatus(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsUsableSessionStatus(MediaPlaybackStatus status)
    {
        return status is MediaPlaybackStatus.Playing
            or MediaPlaybackStatus.Paused
            or MediaPlaybackStatus.Changing
            or MediaPlaybackStatus.Opened;
    }

    private static (MediaPlaybackStatus Status, bool IsPlaybackPositionEnabled) GetSafePlaybackInfo(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo = session.GetPlaybackInfo();
            if (playbackInfo is null)
            {
                return (MediaPlaybackStatus.Closed, false);
            }

            MediaPlaybackStatus status = MapPlaybackStatus(playbackInfo.PlaybackStatus);
            bool isPositionEnabled = playbackInfo.Controls?.IsPlaybackPositionEnabled ?? false;
            return (status, isPositionEnabled);
        }
        catch
        {
            return (MediaPlaybackStatus.Closed, false);
        }
    }

    private static MediaPlaybackStatus GetSafePlaybackStatus(GlobalSystemMediaTransportControlsSession session)
    {
        return GetSafePlaybackInfo(session).Status;
    }

    private static MediaPlaybackStatus MapPlaybackStatus(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackStatus.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            _ => MediaPlaybackStatus.Closed
        };
    }

    private async Task RefreshActiveSessionAsync(bool forceArtworkReload)
    {
        GlobalSystemMediaTransportControlsSession? session;
        int version;

        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning)
            {
                return;
            }

            session = _activeSession;
            version = ++_refreshVersion;
        }

        if (session is null)
        {
            PublishStateIfCurrent(version, MediaState.Inactive);
            return;
        }

        (MediaPlaybackStatus playbackStatus, bool isPlaybackPositionEnabled) = GetSafePlaybackInfo(session);
        if (!IsUsableSessionStatus(playbackStatus))
        {
            PublishStateIfCurrent(version, MediaState.Inactive);
            return;
        }

        MediaTimeline? timeline = ReadSafeTimeline(session, isPlaybackPositionEnabled);
        TimeSpan? position = timeline is { HasValidTimeline: true } ? timeline.Position - timeline.StartTime : null;
        TimeSpan? duration = timeline is { HasValidTimeline: true } ? timeline.Duration : null;

        GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
        try
        {
            properties = await session.TryGetMediaPropertiesAsync();
        }
        catch
        {
            properties = null;
        }

        if (!IsRefreshVersionCurrent(version))
        {
            return;
        }

        string rawTitle = properties?.Title?.Trim() ?? string.Empty;
        string rawArtist = properties?.Artist?.Trim() ?? string.Empty;
        string rawAlbum = properties?.AlbumTitle?.Trim() ?? string.Empty;

        bool isPlaying = playbackStatus == MediaPlaybackStatus.Playing;
        bool hasAnyTextMetadata = !string.IsNullOrEmpty(rawTitle)
            || !string.IsNullOrEmpty(rawArtist)
            || !string.IsNullOrEmpty(rawAlbum);

        // If a session is merely Opened/Changing with zero metadata and not playing/paused, treat as inactive.
        if (!hasAnyTextMetadata && playbackStatus is not (MediaPlaybackStatus.Playing or MediaPlaybackStatus.Paused))
        {
            PublishStateIfCurrent(version, MediaState.Inactive);
            return;
        }

        string effectiveTitle = !string.IsNullOrEmpty(rawTitle)
            ? rawTitle
            : (!string.IsNullOrEmpty(rawAlbum) ? rawAlbum : FallbackTrackTitle);

        string sourceAppId = GetSafeSourceAppId(session);
        string trackIdentityKey = $"{sourceAppId}|{effectiveTitle}|{rawArtist}|{rawAlbum}";

        byte[]? artworkBytes = await ResolveArtworkBytesAsync(
            properties?.Thumbnail,
            trackIdentityKey,
            forceArtworkReload,
            version);

        if (!IsRefreshVersionCurrent(version))
        {
            return;
        }

        MediaTrack track = new(
            Title: effectiveTitle,
            Artist: rawArtist,
            Album: rawAlbum,
            Artwork: artworkBytes);

        MediaState nextState = new(
            IsAvailable: true,
            IsPlaying: isPlaying,
            PlaybackStatus: playbackStatus,
            Track: track,
            Position: position,
            Duration: duration,
            Timeline: timeline);

        PublishStateIfCurrent(version, nextState);
    }

    private async Task<byte[]?> ResolveArtworkBytesAsync(
        IRandomAccessStreamReference? thumbnailReference,
        string trackIdentityKey,
        bool forceArtworkReload,
        int expectedVersion)
    {
        lock (_syncLock)
        {
            if (!forceArtworkReload &&
                string.Equals(_cachedArtworkTrackKey, trackIdentityKey, StringComparison.Ordinal))
            {
                return _cachedArtworkBytes;
            }
        }

        byte[]? loadedBytes = null;
        if (thumbnailReference is not null)
        {
            loadedBytes = await ReadThumbnailBytesSafeAsync(thumbnailReference);
        }

        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning || _refreshVersion != expectedVersion)
            {
                return loadedBytes;
            }

            // Preserve previously cached artwork if the same track fired a transient metadata update
            // where the thumbnail stream was momentarily null.
            if (loadedBytes is null &&
                string.Equals(_cachedArtworkTrackKey, trackIdentityKey, StringComparison.Ordinal))
            {
                return _cachedArtworkBytes;
            }

            _cachedArtworkTrackKey = trackIdentityKey;
            _cachedArtworkBytes = loadedBytes;
            return loadedBytes;
        }
    }

    private static async Task<byte[]?> ReadThumbnailBytesSafeAsync(IRandomAccessStreamReference thumbnailReference)
    {
        try
        {
            using IRandomAccessStreamWithContentType randomAccessStream = await thumbnailReference.OpenReadAsync();
            if (randomAccessStream is null || randomAccessStream.Size == 0 || randomAccessStream.Size > MaxArtworkByteLength)
            {
                return null;
            }

            using Stream managedStream = randomAccessStream.AsStreamForRead();
            using MemoryStream memoryStream = new((int)randomAccessStream.Size);
            await managedStream.CopyToAsync(memoryStream);
            byte[] bytes = memoryStream.ToArray();
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static MediaTimeline? ReadSafeTimeline(
        GlobalSystemMediaTransportControlsSession session,
        bool isPlaybackPositionEnabled)
    {
        try
        {
            GlobalSystemMediaTransportControlsSessionTimelineProperties? raw = session.GetTimelineProperties();
            if (raw is null)
            {
                return null;
            }

            TimeSpan startTime = raw.StartTime >= TimeSpan.Zero ? raw.StartTime : TimeSpan.Zero;
            TimeSpan endTime = raw.EndTime;
            TimeSpan duration = endTime - startTime;

            if (endTime <= startTime || duration <= TimeSpan.Zero)
            {
                return null;
            }

            TimeSpan position = raw.Position;
            if (position < startTime)
            {
                position = startTime;
            }
            else if (position > endTime)
            {
                position = endTime;
            }

            TimeSpan minSeek = raw.MinSeekTime;
            TimeSpan maxSeek = raw.MaxSeekTime;

            // Many sessions leave MinSeekTime/MaxSeekTime at 00:00:00 while reporting valid StartTime/EndTime
            // and IsPlaybackPositionEnabled = true. Normalize bounds safely.
            if (minSeek == TimeSpan.Zero && maxSeek == TimeSpan.Zero)
            {
                minSeek = startTime;
                maxSeek = endTime;
            }
            else
            {
                if (minSeek < startTime)
                {
                    minSeek = startTime;
                }

                if (maxSeek > endTime || maxSeek == TimeSpan.Zero)
                {
                    maxSeek = endTime;
                }
            }

            bool hasValidSeekRange = maxSeek > minSeek;
            bool seekSupported = isPlaybackPositionEnabled && hasValidSeekRange;

            return new MediaTimeline(
                StartTime: startTime,
                EndTime: endTime,
                Position: position,
                MinSeekTime: hasValidSeekRange ? minSeek : startTime,
                MaxSeekTime: hasValidSeekRange ? maxSeek : endTime,
                LastUpdatedTime: raw.LastUpdatedTime,
                IsSeekSupported: seekSupported);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSafeSourceAppId(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.SourceAppUserModelId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool IsRunningSafe()
    {
        lock (_syncLock)
        {
            return !_isDisposed && _isRunning;
        }
    }

    private bool IsRefreshVersionCurrent(int version)
    {
        lock (_syncLock)
        {
            return !_isDisposed && _isRunning && _refreshVersion == version;
        }
    }

    private void PublishStateIfCurrent(int version, MediaState nextState)
    {
        EventHandler<MediaState>? handler;

        lock (_syncLock)
        {
            if (_isDisposed || !_isRunning || _refreshVersion != version)
            {
                return;
            }

            if (!nextState.IsAvailable)
            {
                _cachedArtworkTrackKey = null;
                _cachedArtworkBytes = null;
            }

            if (Equals(CurrentState, nextState))
            {
                return;
            }

            CurrentState = nextState;
            handler = MediaStateChanged;
        }

        handler?.Invoke(this, nextState);
    }

    private void DetachSessionManagerLocked()
    {
        if (_sessionManager is null)
        {
            return;
        }

        try
        {
            _sessionManager.CurrentSessionChanged -= OnSessionManagerCurrentSessionChanged;
            _sessionManager.SessionsChanged -= OnSessionManagerSessionsChanged;
        }
        catch
        {
            // Ignore COM/WinRT teardown errors on exit.
        }

        _sessionManager = null;
    }

    private void DetachActiveSessionMetadataLocked()
    {
        if (_activeSession is null)
        {
            return;
        }

        try
        {
            _activeSession.MediaPropertiesChanged -= OnActiveSessionMediaPropertiesChanged;
            _activeSession.TimelinePropertiesChanged -= OnActiveSessionTimelinePropertiesChanged;
        }
        catch
        {
            // Session may have already closed.
        }

        _activeSession = null;
    }

    private void DetachAllSessionsLocked()
    {
        DetachActiveSessionMetadataLocked();

        foreach (GlobalSystemMediaTransportControlsSession session in _monitoredSessions)
        {
            try
            {
                session.PlaybackInfoChanged -= OnMonitoredSessionPlaybackInfoChanged;
            }
            catch
            {
                // Ignore disconnected session teardown errors.
            }
        }

        _monitoredSessions.Clear();
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            Stop();
            _isDisposed = true;
        }
    }
}
