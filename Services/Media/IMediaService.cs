using System;
using System.Threading.Tasks;

namespace WindowsNotch.Services.Media;

/// <summary>
/// Event-driven system media service contract.
/// </summary>
public interface IMediaService : IDisposable
{
    /// <summary>
    /// Gets the latest system media state snapshot.
    /// </summary>
    MediaState CurrentState { get; }

    /// <summary>
    /// Raised whenever the relevant media session, playback status, metadata, or timeline changes.
    /// </summary>
    event EventHandler<MediaState>? MediaStateChanged;

    /// <summary>
    /// Starts listening to Windows system media session events.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops listening to Windows system media session events and releases active session subscriptions.
    /// </summary>
    void Stop();

    /// <summary>
    /// Requests the active media session to begin or resume playback.
    /// </summary>
    Task<bool> PlayAsync();

    /// <summary>
    /// Requests the active media session to pause playback.
    /// </summary>
    Task<bool> PauseAsync();

    /// <summary>
    /// Requests the active media session to toggle between play and pause based on its current playback state.
    /// </summary>
    Task<bool> TogglePlayPauseAsync();

    /// <summary>
    /// Requests the active media session to skip to the next track.
    /// </summary>
    Task<bool> SkipNextAsync();

    /// <summary>
    /// Requests the active media session to skip to the previous track.
    /// </summary>
    Task<bool> SkipPreviousAsync();

    /// <summary>
    /// Requests the active media session to seek to the specified playback position.
    /// </summary>
    Task<bool> SeekAsync(TimeSpan requestedPosition);
}
