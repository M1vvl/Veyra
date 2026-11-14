using System;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using GlassDock.Models;

namespace GlassDock.Music;

/// <summary>
/// Windows Media Session 服务 - 获取当前播放的音乐信息
/// </summary>
public class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly object _sessionGate=new();
    private readonly NeteaseBackgroundClock _nativeClock=new();
    private TimeSpan _knownDuration;
    private string? _knownTrack;
    private TimeSpan? _pausedNativePosition;

    public event EventHandler<MediaInfo>? MediaChanged;
    public event EventHandler? PlaybackStatusChanged;

    public async Task InitializeAsync()
    {
        try
        {
            if(_disposed)return;
            var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(_lifetime.Token);
            lock(_sessionGate)
            {
                if(_disposed)return;
                _sessionManager=manager;
                _sessionManager.CurrentSessionChanged += OnSessionChanged;
                UpdateCurrentSessionLocked();
            }
        }
        catch (OperationCanceledException) when(_disposed) { }
        catch (Exception ex)
        {
            Program.Log($"MediaSessionService 初始化失败: {ex.Message}");
        }
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateCurrentSession();
    }

    private void UpdateCurrentSession()
    {
        lock(_sessionGate)UpdateCurrentSessionLocked();
    }

    private void UpdateCurrentSessionLocked()
    {
        if(_disposed)return;
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _currentSession = _sessionManager?.GetCurrentSession();

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelineChanged;

            // 立即获取信息
            _ = Task.Run(async () =>
            {
                var info = await GetCurrentMediaInfoAsync();
                if (info != null && !_disposed)
                {
                    MediaChanged?.Invoke(this, info);
                }
            });
        }
    }

    public async Task<MediaInfo?> GetCurrentMediaInfoAsync()
    {
        var session=_currentSession;
        if(_disposed || session is null)return null;
        try
        {
            var properties=await session.TryGetMediaPropertiesAsync().AsTask(_lifetime.Token);
            if(_disposed || !ReferenceEquals(session,_currentSession))return null;
            var playbackInfo=session.GetPlaybackInfo();
            var timeline=session.GetTimelineProperties();

            // 获取封面
            byte[]? thumbnailData = null;
            if (properties.Thumbnail != null)
            {
                try
                {
                    using var stream = await properties.Thumbnail.OpenReadAsync().AsTask(_lifetime.Token);
                    if(_disposed)return null;
                    thumbnailData = new byte[stream.Size];
                    await stream.ReadAsync(thumbnailData.AsBuffer(), (uint)stream.Size, InputStreamOptions.None).AsTask(_lifetime.Token);
                }
                catch
                {
                    // 封面读取失败，继续
                }
            }

            if(_disposed)return null;
            var media = new MediaInfo
            {
                Title = properties.Title ?? "",
                Artist = properties.Artist ?? "",
                AlbumTitle = properties.AlbumTitle ?? "",
                Thumbnail = thumbnailData,
                Position = timeline.Position,
                Duration = timeline.EndTime - timeline.StartTime,
                IsPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                LastUpdated = DateTime.Now
            };
            if(session.SourceAppUserModelId.Contains("cloudmusic",StringComparison.OrdinalIgnoreCase))
                NeteaseQueueMetadata.Complete(media);
            if(_knownTrack!=media.GetTrackKey())_pausedNativePosition=null;
            _knownTrack=media.GetTrackKey();
            _knownDuration=media.Duration;
            return media;
        }
        catch (OperationCanceledException) when(_disposed) {return null;}
        catch (Exception ex)
        {
            Program.Log($"获取媒体信息失败: {ex.Message}");
            return null;
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        // 歌曲切换
        _ = Task.Run(async () =>
        {
            var info = await GetCurrentMediaInfoAsync();
            if (info != null && !_disposed)
            {
                MediaChanged?.Invoke(this, info);
            }
        });
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        // 播放状态改变
        PlaybackStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        PlaybackStatusChanged?.Invoke(this,EventArgs.Empty);
    }

    public (TimeSpan Position,TimeSpan Duration,bool Playing,bool CanSeek)? ReadTimeline()
    {
        var session=_currentSession;
        if(session is null)return null;
        try
        {
            var timeline=session.GetTimelineProperties();
            var playback=session.GetPlaybackInfo();
            bool playing=playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var position=timeline.Position-timeline.StartTime;
            if(playing && timeline.LastUpdatedTime!=default)
            {
                var elapsed=DateTimeOffset.Now-timeline.LastUpdatedTime;
                if(elapsed>TimeSpan.Zero)position+=TimeSpan.FromSeconds(elapsed.TotalSeconds*(playback.PlaybackRate??1));
            }
            var duration=timeline.EndTime-timeline.StartTime;
            if(duration<=TimeSpan.Zero && session.SourceAppUserModelId.Contains("cloudmusic",StringComparison.OrdinalIgnoreCase))
            {
                var native=_nativeClock.Read();
                if(native is not null && _knownDuration>TimeSpan.Zero && native.Seconds<=_knownDuration.TotalSeconds+1)
                {
                    var observed=TimeSpan.FromSeconds(Math.Min(native.Seconds,_knownDuration.TotalSeconds));
                    if(playing)_pausedNativePosition=null;
                    else _pausedNativePosition??=observed;
                    return (_pausedNativePosition??observed,_knownDuration,playing,false);
                }
            }
            position=TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds,0,Math.Max(0,duration.TotalSeconds)));
            return (position,duration,playing,playback.Controls.IsPlaybackPositionEnabled);
        }
        catch(Exception ex) {Program.Log("Media timeline: "+ex.Message); return null;}
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session=_currentSession;
        if(session is null)return false;
        try {return await session.TryChangePlaybackPositionAsync(position.Ticks+session.GetTimelineProperties().StartTime.Ticks);}
        catch(Exception ex) {Program.Log("Media seek: "+ex.Message); return false;}
    }

    public async Task PreviousAsync() { try { if(_currentSession is { } session) await session.TrySkipPreviousAsync(); } catch(Exception ex) { Program.Log("Media previous: "+ex.Message); } }
    public async Task NextAsync() { try { if(_currentSession is { } session) await session.TrySkipNextAsync(); } catch(Exception ex) { Program.Log("Media next: "+ex.Message); } }
    public async Task TogglePlaybackAsync() { try { if(_currentSession is { } session) await session.TryTogglePlayPauseAsync(); } catch(Exception ex) { Program.Log("Media toggle: "+ex.Message); } }

    public void Dispose()
    {
        lock(_sessionGate)DisposeLocked();
    }

    private void DisposeLocked()
    {
        if (_disposed) return;
        _disposed = true;
        _=_lifetime.CancelAsync();
        _nativeClock.Dispose();
        MediaChanged=null;
        PlaybackStatusChanged=null;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        if (_sessionManager != null)
        {
            _sessionManager.CurrentSessionChanged -= OnSessionChanged;
        }

        _currentSession = null;
        _sessionManager = null;
    }
}
