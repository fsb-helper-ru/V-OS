using VOS.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace VOS.Platform.Media;

/// <summary>Snapshot of the current system media session (any player that integrates with Windows media controls).</summary>
public sealed record MediaState
{
    public bool HasSession { get; init; }
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SourceAppId { get; init; } = "";
    public bool IsPlaying { get; init; }
    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanSeek { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionUpdated { get; init; }
    public byte[]? Artwork { get; init; }
    public string ArtworkKey => $"{SourceAppId}|{Title}|{Artist}|{Album}";

    /// <summary>Position extrapolated from the last timeline update while playing.</summary>
    public TimeSpan EstimatedPosition(DateTimeOffset now)
    {
        if (!IsPlaying || Duration <= TimeSpan.Zero) return Position;
        var p = Position + (now - PositionUpdated);
        return p > Duration ? Duration : p;
    }

    public static MediaState Empty { get; } = new();
}

/// <summary>
/// Windows media sessions (GlobalSystemMediaTransportControlsSessionManager): the supported way to show and control
/// Spotify, browsers (YouTube Music, SoundCloud), Apple Music and any player that integrates with Windows.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private string _lastArtworkKey = "";
    private byte[]? _lastArtwork;

    public MediaState Current { get; private set; } = MediaState.Empty;
    public event Action<MediaState>? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => { Attach(_manager.GetCurrentSession()); _ = RefreshAsync(); };
            _manager.SessionsChanged += (_, _) => { if (_session == null) Attach(_manager.GetCurrentSession()); _ = RefreshAsync(); };
            Attach(_manager.GetCurrentSession());
            await RefreshAsync();
        }
        catch (Exception ex) { Log.Warn("Media sessions unavailable", ex); }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? s)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
            _session.TimelinePropertiesChanged -= OnChanged;
        }
        _session = s;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnChanged;
            _session.PlaybackInfoChanged += OnChanged;
            _session.TimelinePropertiesChanged += OnChanged;
        }
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    public async Task RefreshAsync()
    {
        if (!await _refresh.WaitAsync(0)) return;
        try
        {
            var s = _session;
            if (s == null) { Publish(MediaState.Empty); return; }
            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            var key = $"{s.SourceAppUserModelId}|{props?.Title}|{props?.Artist}|{props?.AlbumTitle}";
            if (key != _lastArtworkKey)
            {
                _lastArtworkKey = key;
                _lastArtwork = props?.Thumbnail != null ? await ReadAll(props.Thumbnail) : null;
            }
            var controls = playback?.Controls;
            Publish(new MediaState
            {
                HasSession = true,
                Title = props?.Title ?? "",
                Artist = props?.Artist ?? props?.AlbumArtist ?? "",
                Album = props?.AlbumTitle ?? "",
                SourceAppId = s.SourceAppUserModelId ?? "",
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanPlayPause = controls?.IsPlayPauseToggleEnabled == true || controls?.IsPlayEnabled == true || controls?.IsPauseEnabled == true,
                CanNext = controls?.IsNextEnabled == true,
                CanPrevious = controls?.IsPreviousEnabled == true,
                CanSeek = controls?.IsPlaybackPositionEnabled == true,
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline != null ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
                PositionUpdated = timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
                Artwork = _lastArtwork,
            });
        }
        catch (Exception ex) { Log.Debug("Media refresh failed: " + ex.Message); }
        finally { _refresh.Release(); }
    }

    private void Publish(MediaState s)
    {
        if (s == Current) return;
        Current = s;
        Changed?.Invoke(s);
    }

    private static async Task<byte[]?> ReadAll(IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
            var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
            var read = await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
            var bytes = new byte[read.Length];
            using (var reader = DataReader.FromBuffer(read)) reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    public Task PlayPauseAsync() => Do(s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync() => Do(s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync() => Do(s => s.TrySkipPreviousAsync().AsTask());
    public Task PlayAsync() => Do(s => s.TryPlayAsync().AsTask());
    public Task PauseAsync() => Do(s => s.TryPauseAsync().AsTask());
    public Task SeekAsync(TimeSpan position) => Do(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    private async Task Do(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var s = _session;
        if (s == null) return;
        try { await action(s); } catch (Exception ex) { Log.Debug("Media command failed: " + ex.Message); }
    }

    public void Dispose() => Attach(null);
}
