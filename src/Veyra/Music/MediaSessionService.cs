using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using GlassDock.Models;

namespace GlassDock.Music;

/// <summary>Serial metadata and timeline readers, owned by the island lifetime.</summary>
public sealed class MediaSessionService : IDisposable
{
    internal sealed record Timeline(TimeSpan Position,TimeSpan Duration,bool Playing,bool CanSeek,long Revision,bool Estimated=false)
    {
        internal long ObservedAt {get;}=Environment.TickCount64;
        internal DateTimeOffset SourceUpdatedAt {get;init;}
    }
    private sealed record PlaybackAnchor(long Revision,double Seconds,bool Playing,long At,long Expires);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly SemaphoreSlim _refresh=new(0,1);
    private readonly SemaphoreSlim _timelineRefresh=new(0,1);
    private readonly object _gate=new();
    private readonly NeteaseBackgroundClock _nativeClock=new();
    private readonly MediaTimelineGuard _timelineGuard=new();
    private readonly MediaArtworkService _artwork=new();
    private CancellationTokenSource? _coverLifetime;
    private long _coverRevision=-1,_coverRetryAt;
    private bool _coverInFlight;
    private int _coverAttempts;
    private MediaInfo? _media;
    private Timeline? _timeline;
    private Timeline? _lastTimeline;
    private PlaybackAnchor? _anchor;
    private Task? _metadataWorker,_timelineWorker;
    private long _sessionRevision,_propertyRevision,_trackRevision,_publishedSessionRevision=-1;
    private long _selectionAfter;
    private long _queueRetryAt;
    private DateTimeOffset _trackChangedAt;
    private TimeSpan? _pausedNativePosition;
    private volatile bool _disposed;

    public event EventHandler<MediaInfo>? MediaChanged;
    public event EventHandler? PlaybackStatusChanged;

    public async Task InitializeAsync()
    {
        try
        {
            var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(_lifetime.Token);
            lock(_gate)
            {
                if(_disposed || _manager is not null)return;
                _manager=manager;
                manager.CurrentSessionChanged+=OnSessionChanged;
                manager.SessionsChanged+=OnSessionsChanged;
                UpdateSessionLocked();
                _metadataWorker=Task.Run(ReadMetadataLoopAsync);
                _timelineWorker=Task.Run(ReadTimelineLoopAsync);
            }
        }
        catch(OperationCanceledException) when(_disposed) { }
        catch(Exception ex) {Program.Log("Media initialization: "+ex.Message);}
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,CurrentSessionChangedEventArgs args)
    {
        lock(_gate)UpdateSessionLocked();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,SessionsChangedEventArgs args)
    {
        lock(_gate)UpdateSessionLocked();
    }

    private void UpdateSessionLocked()
    {
        if(_disposed)return;
        var next=_manager is { } manager?MediaSessionSelection.Select(manager,_session):null;
        if(MediaSessionSelection.Same(next,_session))return;
        if(_session is { } previous)
        {
            previous.MediaPropertiesChanged-=OnPropertiesChanged;
            previous.PlaybackInfoChanged-=OnPlaybackChanged;
            previous.TimelinePropertiesChanged-=OnTimelineChanged;
        }
        _session=next;
        _sessionRevision++;
        _propertyRevision++;
        _timeline=null;
        _anchor=null;
        _timelineGuard.Clear();
        _pausedNativePosition=null;
        CancelCover();
        if(next is not null)
        {
            next.MediaPropertiesChanged+=OnPropertiesChanged;
            next.PlaybackInfoChanged+=OnPlaybackChanged;
            next.TimelinePropertiesChanged+=OnTimelineChanged;
        }
        else
        {
            _media=null;
            MediaChanged?.Invoke(this,new MediaInfo {Revision=++_trackRevision});
        }
        RequestRefresh();
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,MediaPropertiesChangedEventArgs args)
    {
        lock(_gate)
        {
            if(!MediaSessionSelection.Same(sender,_session) || _disposed)return;
            _propertyRevision++;
            RequestRefresh();
        }
    }

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender,PlaybackInfoChangedEventArgs args)
    {
        if(!MediaSessionSelection.Same(sender,_session) || _disposed)return;
        RequestRefresh();
        WakeTimeline();
        PlaybackStatusChanged?.Invoke(this,EventArgs.Empty);
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender,TimelinePropertiesChangedEventArgs args)
    {
        if(MediaSessionSelection.Same(sender,_session) && !_disposed)WakeTimeline();
    }

    private void WakeTimeline()
    {
        if(_disposed)return;
        try {_timelineRefresh.Release();}
        catch(SemaphoreFullException) { }
        catch(ObjectDisposedException) when(_disposed) { }
    }

    private void RequestRefresh()
    {
        if(_disposed)return;
        try {_refresh.Release();}
        catch(SemaphoreFullException) { }
        catch(ObjectDisposedException) when(_disposed) { }
    }

    private async Task ReadMetadataLoopAsync()
    {
        try
        {
            while(!_disposed)
            {
                int interval=Volatile.Read(ref _timeline)?.Playing==true?50:200;
                await _refresh.WaitAsync(TimeSpan.FromMilliseconds(interval),_lifetime.Token).ConfigureAwait(false);
                lock(_gate)
                {
                    if(Environment.TickCount64>=_selectionAfter)
                    {
                        UpdateSessionLocked();
                        _selectionAfter=Environment.TickCount64+250;
                    }
                }
                var session=_session;
                long sessionRevision=Volatile.Read(ref _sessionRevision);
                long propertyRevision=Volatile.Read(ref _propertyRevision);
                if(session is null)continue;
                try
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var properties=await session.TryGetMediaPropertiesAsync().AsTask(timeout.Token).ConfigureAwait(false);
                    var playback=session.GetPlaybackInfo();
                    var raw=session.GetTimelineProperties();
                    var media=new MediaInfo {
                        SourceAppId=session.SourceAppUserModelId,Title=properties.Title??"",
                        Artist=properties.Artist??"",AlbumTitle=properties.AlbumTitle??"",
                        Position=raw.Position-raw.StartTime,Duration=raw.EndTime-raw.StartTime,
                        IsPlaying=playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                        LastUpdated=DateTime.Now
                    };
                    var previous=Volatile.Read(ref _media);
                    bool sameSong=previous is not null && previous.SourceAppId==media.SourceAppId
                        && previous.Title==media.Title && previous.Artist==media.Artist;
                    if(sameSong)
                    {
                        media.SourceTrackId=previous!.SourceTrackId;
                        media.ArtworkUrl=previous.ArtworkUrl;
                        if(media.Duration<=TimeSpan.Zero)media.Duration=previous.Duration;
                    }
                    if(IsNetease(session) && (!sameSong || (media.SourceTrackId<=0 || media.ArtworkUrl is null)
                        && Environment.TickCount64>=_queueRetryAt))
                    {
                        NeteaseQueueMetadata.Complete(media);
                        _queueRetryAt=Environment.TickCount64+500;
                    }
                    lock(_gate)
                    {
                        // Discard a request overtaken by a song/session notification.
                        if(_disposed || !ReferenceEquals(session,_session)
                            || sessionRevision!=_sessionRevision || propertyRevision!=_propertyRevision)continue;
                        bool changed=!sameSong || previous is not null && previous.SourceTrackId>0
                            && media.SourceTrackId>0 && previous.SourceTrackId!=media.SourceTrackId
                            || _publishedSessionRevision!=sessionRevision;
                        bool enriched=previous is not null && (previous.SourceTrackId!=media.SourceTrackId
                            || previous.ArtworkUrl!=media.ArtworkUrl || previous.AlbumTitle!=media.AlbumTitle
                            || previous.Duration!=media.Duration);
                        if(changed)
                        {
                            _publishedSessionRevision=sessionRevision;
                            media.Revision=++_trackRevision;
                            _trackChangedAt=previous is null?DateTimeOffset.MinValue:DateTimeOffset.UtcNow;
                            _pausedNativePosition=null;
                            if(IsNetease(session))_nativeClock.TrackChanged($"{media.Title}|{media.Artist}");
                            if(previous?.SourceAppId==media.SourceAppId && (_timeline??_lastTimeline) is { } prior)
                                _timelineGuard.Begin(prior.Position.TotalSeconds,prior.Duration.TotalSeconds,
                                    prior.SourceUpdatedAt,DateTimeOffset.UtcNow);
                            else _timelineGuard.Clear();
                            _timeline=null;
                            // Bridge the short native-reader handover at a confirmed song change.
                            long now=Environment.TickCount64;
                            bool restarted=previous is not null && previous.SourceAppId==media.SourceAppId;
                            _anchor=restarted?new PlaybackAnchor(media.Revision,0,media.IsPlaying,now,now+6000):null;
                            if(restarted)_timeline=new Timeline(TimeSpan.Zero,media.Duration,media.IsPlaying,false,media.Revision,true);
                            if(previous is not null)media.Position=TimeSpan.Zero;
                            Volatile.Write(ref _media,media);
                            CancelCover();
                            _coverAttempts=0;
                            // Publish identity before opening the artwork stream.
                            MediaChanged?.Invoke(this,Copy(media));
                            WakeTimeline();
                        }
                        else if(enriched)
                        {
                            media.Revision=previous!.Revision;
                            media.Thumbnail=previous.Thumbnail;
                            Volatile.Write(ref _media,media);
                            MediaChanged?.Invoke(this,Copy(media));
                        }
                        else media=Copy(_media!);
                        bool coverChanged=propertyRevision!=_coverRevision;
                        bool retryCover=media.Thumbnail is null && _coverAttempts<4
                            && !_coverInFlight && Environment.TickCount64>=_coverRetryAt;
                        if((changed || coverChanged || enriched || retryCover) && !string.IsNullOrEmpty(media.Title))
                        {
                            CancelCover();
                            _coverRevision=propertyRevision;
                            _coverRetryAt=Environment.TickCount64+1000;
                            _coverAttempts++;
                            _coverInFlight=true;
                            _coverLifetime=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                            _coverLifetime.CancelAfter(TimeSpan.FromSeconds(3));
                            _=ReadCoverAsync(properties.Thumbnail,media,_coverLifetime);
                        }
                    }
                }
                catch(OperationCanceledException) { }
                catch(Exception ex) {Program.Log("Media metadata: "+ex.Message);}
            }
        }
        catch(OperationCanceledException) { }
    }

    private async Task ReadCoverAsync(IRandomAccessStreamReference? thumbnail,MediaInfo media,CancellationTokenSource lifetime)
    {
        try
        {
            var bytes=await _artwork.ReadAsync(media,thumbnail,lifetime.Token).ConfigureAwait(false);
            if(bytes is null)return;
            lock(_gate)
            {
                if(_disposed || lifetime.IsCancellationRequested || _media?.Revision!=media.Revision
                    || !ReferenceEquals(lifetime,_coverLifetime))return;
                if(_media.Thumbnail is { } previous && previous.AsSpan().SequenceEqual(bytes))return;
                var updated=Copy(media);
                updated.Thumbnail=bytes;
                Volatile.Write(ref _media,updated);
                MediaChanged?.Invoke(this,Copy(updated));
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) {Program.Log("Media artwork: "+ex.Message);}
        finally
        {
            lock(_gate)
            {
                if(ReferenceEquals(lifetime,_coverLifetime)) {_coverLifetime=null;_coverInFlight=false;}
            }
            lifetime.Dispose();
        }
    }

    private async Task ReadTimelineLoopAsync()
    {
        try
        {
            while(!_disposed)
            {
                int delay=25;
                try
                {
                    var session=_session;
                    var media=Volatile.Read(ref _media);
                    if(session is not null && media is not null && media.SourceAppId==session.SourceAppUserModelId
                        && Volatile.Read(ref _publishedSessionRevision)==Volatile.Read(ref _sessionRevision))
                    {
                        var playback=session.GetPlaybackInfo();
                        var raw=session.GetTimelineProperties();
                        bool playing=playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        delay=playing?25:100;
                        var duration=raw.EndTime-raw.StartTime;
                        var position=raw.Position-raw.StartTime;
                        bool canSeek=playback.Controls.IsPlaybackPositionEnabled;
                        bool valid;
                        bool estimated=false;
                        if(duration<=TimeSpan.Zero && IsNetease(session))
                        {
                            var native=_nativeClock.Read(playing);
                            duration=media.Duration;
                            valid=native is not null && duration>TimeSpan.Zero && native.Seconds<=duration.TotalSeconds+1;
                            if(valid)
                            {
                                position=TimeSpan.FromSeconds(native!.Seconds);
                                if(playing)_pausedNativePosition=null;
                                else _pausedNativePosition??=position;
                                position=_pausedNativePosition??position;
                            }
                            else if(Volatile.Read(ref _anchor) is { } anchor && anchor.Revision==media.Revision
                                && Environment.TickCount64<=anchor.Expires)
                            {
                                long now=Environment.TickCount64;
                                position=TimeSpan.FromSeconds(anchor.Seconds+(anchor.Playing?Math.Max(0,now-anchor.At)/1000d:0));
                                valid=duration>TimeSpan.Zero;
                                estimated=true;
                            }
                            canSeek=false;
                        }
                        else
                        {
                            // SMTC can publish identity before replacing the preceding timeline.
                            valid=_trackChangedAt==DateTimeOffset.MinValue
                                || raw.LastUpdatedTime>=_trackChangedAt-TimeSpan.FromMilliseconds(150);
                            lock(_gate)
                            {
                                valid=valid && ReferenceEquals(session,_session) && media.Revision==_media?.Revision
                                    && _timelineGuard.Accept(position.TotalSeconds,duration.TotalSeconds,
                                    raw.LastUpdatedTime,DateTimeOffset.UtcNow);
                            }
                            if(playing && raw.LastUpdatedTime!=default)
                            {
                                double elapsed=Math.Max(0,(DateTimeOffset.UtcNow-raw.LastUpdatedTime).TotalSeconds);
                                position+=TimeSpan.FromSeconds(elapsed*(playback.PlaybackRate??1));
                            }
                        }
                        if(valid)
                        {
                            position=TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds,0,Math.Max(0,duration.TotalSeconds)));
                            lock(_gate)
                            {
                                if(!_disposed && ReferenceEquals(session,_session) && media.Revision==_media?.Revision)
                                {
                                    long now=Environment.TickCount64;
                                    long expires=estimated?(_anchor?.Expires??now):now+1000;
                                    _anchor=new PlaybackAnchor(media.Revision,position.TotalSeconds,playing,now,expires);
                                    var snapshot=new Timeline(position,duration,playing,canSeek,media.Revision,estimated)
                                        {SourceUpdatedAt=raw.LastUpdatedTime};
                                    bool playbackChanged=_timeline?.Playing!=playing;
                                    _lastTimeline=snapshot;
                                    Volatile.Write(ref _timeline,snapshot);
                                    if(playbackChanged)PlaybackStatusChanged?.Invoke(this,EventArgs.Empty);
                                }
                            }
                        }
                    }
                }
                catch(Exception ex) {Program.Log("Media timeline: "+ex.Message);}
                await _timelineRefresh.WaitAsync(TimeSpan.FromMilliseconds(delay),_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException) { }
    }

    private static bool IsNetease(GlobalSystemMediaTransportControlsSession session)
        =>session.SourceAppUserModelId.Contains("cloudmusic",StringComparison.OrdinalIgnoreCase);

    private static MediaInfo Copy(MediaInfo media)=>new() {
        SourceAppId=media.SourceAppId,SourceTrackId=media.SourceTrackId,Revision=media.Revision,
        Title=media.Title,Artist=media.Artist,AlbumTitle=media.AlbumTitle,Thumbnail=media.Thumbnail,
        ArtworkUrl=media.ArtworkUrl,
        Position=media.Position,Duration=media.Duration,IsPlaying=media.IsPlaying,LastUpdated=media.LastUpdated
    };

    public Task<MediaInfo?> GetCurrentMediaInfoAsync()
    {
        var media=Volatile.Read(ref _media);
        return Task.FromResult(media is null?null:Copy(media));
    }

    internal Timeline? ReadTimeline()
    {
        var timeline=Volatile.Read(ref _timeline);
        return timeline is not null && Environment.TickCount64-timeline.ObservedAt<=250?timeline:null;
    }

    internal object InspectTimelineSource()
    {
        var raw=_session?.GetTimelineProperties();
        return new {raw?.Position,raw?.StartTime,raw?.EndTime,raw?.LastUpdatedTime,
            sessionRevision=_sessionRevision,trackRevision=_trackRevision,guard=_timelineGuard.Inspect()};
    }

    public async Task<bool> SeekAsync(TimeSpan position)
    {
        var session=_session;
        if(session is null || _disposed)return false;
        try {return await session.TryChangePlaybackPositionAsync(position.Ticks+session.GetTimelineProperties().StartTime.Ticks);}
        catch(Exception ex) {Program.Log("Media seek: "+ex.Message);return false;}
    }

    public async Task PreviousAsync() {try {if(_session is { } session)await session.TrySkipPreviousAsync();RequestRefresh();}catch(Exception ex) {Program.Log("Media previous: "+ex.Message);}}
    public async Task NextAsync() {try {if(_session is { } session)await session.TrySkipNextAsync();RequestRefresh();}catch(Exception ex) {Program.Log("Media next: "+ex.Message);}}
    public async Task TogglePlaybackAsync()
    {
        try
        {
            if(_session is not { } session || _disposed)return;
            bool playing=session.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if(playing)await session.TryPauseAsync();
            else await session.TryPlayAsync();
            RequestRefresh();
            WakeTimeline();
        }
        catch(Exception ex) {Program.Log("Media toggle: "+ex.Message);}
    }

    private void CancelCover()
    {
        var lifetime=_coverLifetime;
        _coverLifetime=null;
        _coverInFlight=false;
        try {lifetime?.Cancel();}catch(ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Task cancellation;
        lock(_gate)
        {
            if(_disposed)return;
            _disposed=true;
            cancellation=_lifetime.CancelAsync();
            CancelCover();
            if(_session is { } session)
            {
                session.MediaPropertiesChanged-=OnPropertiesChanged;
                session.PlaybackInfoChanged-=OnPlaybackChanged;
                session.TimelinePropertiesChanged-=OnTimelineChanged;
            }
            if(_manager is { } manager)
            {
                manager.CurrentSessionChanged-=OnSessionChanged;
                manager.SessionsChanged-=OnSessionsChanged;
            }
            _session=null;
            _manager=null;
            _media=null;
            _timeline=null;
            _lastTimeline=null;
            _anchor=null;
            MediaChanged=null;
            PlaybackStatusChanged=null;
            _nativeClock.Dispose();
        }
        _=Task.WhenAll(cancellation,_metadataWorker??Task.CompletedTask,_timelineWorker??Task.CompletedTask)
            .ContinueWith(_=> {_artwork.Dispose();_refresh.Dispose();_timelineRefresh.Dispose();_lifetime.Dispose();},TaskScheduler.Default);
    }
}
