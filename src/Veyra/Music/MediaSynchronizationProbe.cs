using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.Media.Control;
using GlassDock.Island;
using GlassDock.Models;
using GlassDock.Music.LyricsProviders;

namespace GlassDock.Music;

/// <summary>Focused regressions and opt-in live next/previous verification, without disk artifacts.</summary>
internal static class MediaSynchronizationProbe
{
    internal static int InspectSessions(string[] args)
    {
        int result=1;
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=>
        {
            try
            {
                var manager=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var rows=new List<object>();
                foreach(var session in manager.GetSessions())
                {
                    if(args.Length==2 && session.SourceAppUserModelId.Contains(args[0],StringComparison.OrdinalIgnoreCase))
                    {
                        if(args[1]=="pause")await session.TryPauseAsync();
                        else if(args[1]=="play")await session.TryPlayAsync();
                    }
                    var properties=await session.TryGetMediaPropertiesAsync();
                    var timeline=session.GetTimelineProperties();
                    var playback=session.GetPlaybackInfo();
                    string? nativeArtwork=null;
                    int cachedArtworkBytes=0;
                    double artworkLookupMs=0;
                    if(args.Length==2 && args[1]=="artwork"
                        && session.SourceAppUserModelId.Contains(args[0],StringComparison.OrdinalIgnoreCase))
                    {
                        using var source=new QQMusicArtworkSource();
                        var lookup=Stopwatch.StartNew();
                        nativeArtwork=source.ReadUrl(new MediaInfo {SourceAppId=session.SourceAppUserModelId,
                            Title=properties.Title,Artist=properties.Artist,AlbumTitle=properties.AlbumTitle});
                        if(Uri.TryCreate(nativeArtwork,UriKind.Absolute,out var uri))
                            cachedArtworkBytes=(await source.ReadCachedAsync(uri,CancellationToken.None))?.Length??0;
                        artworkLookupMs=lookup.Elapsed.TotalMilliseconds;
                    }
                    ulong artworkBytes=0;
                    string? artworkType=null;
                    if(properties.Thumbnail is { } artwork)
                    {
                        using var stream=await artwork.OpenReadAsync();
                        artworkBytes=stream.Size;
                        artworkType=stream.ContentType;
                    }
                    rows.Add(new {source=session.SourceAppUserModelId,title=properties.Title,artist=properties.Artist,
                        playing=playback.PlaybackStatus.ToString(),position=timeline.Position.TotalSeconds,
                        duration=(timeline.EndTime-timeline.StartTime).TotalSeconds,timeline.LastUpdatedTime,
                        canSeek=playback.Controls.IsPlaybackPositionEnabled,artworkBytes,artworkType,
                        nativeArtwork,cachedArtworkBytes,artworkLookupMs});
                }
                Console.WriteLine(JsonSerializer.Serialize(rows));
                result=0;
            }
            catch(Exception ex) {Console.WriteLine(JsonSerializer.Serialize(new {error=ex.Message}));}
            finally {app.Shutdown(result);}
        };
        app.Run();return result;
    }

    internal static int Run(bool live)
    {
        int exitCode=1;
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Startup+=async(_,_)=>
        {
            try
            {
                if(live)await CheckLiveAsync();else await CheckViewsAsync();
                exitCode=0;
            }
            catch(Exception ex) {Console.WriteLine(JsonSerializer.Serialize(new {pass=false,error=ex.ToString()}));}
            finally {app.Shutdown(exitCode);}
        };
        app.Run();
        return exitCode;
    }

    private static void Assert(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException(message);
    }

    private static T Field<T>(IslandWindow window,string name)
        =>(T)typeof(IslandWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Set(IslandWindow window,string name,object? value)
        =>typeof(IslandWindow).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(window,value);

    private static object? Call(IslandWindow window,string name,params object?[] args)
        =>typeof(IslandWindow).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,args);

    private static async Task CheckViewsAsync()
    {
        var provider=new DelayedProvider();
        var window=new IslandWindow(new ImprovedLyricsService(new[] {provider}));
        try
        {
            CheckPalette(window);
            var first=new MediaInfo {Title="First track",Revision=1,Duration=TimeSpan.FromSeconds(240),IsPlaying=true};
            var firstLoad=(Task)Call(window,"UpdateMediaInfo",first)!;
            provider.Complete(first.Title,"First lyric");
            await firstLoad;
            Set(window,"_isExpanded",true);
            Call(window,"UpdateExpandedView");
            Field<IslandPlaybackClock>(window,"_lyricClock").Observe(120,true,0);
            Field<IslandSeekBar>(window,"_compactSeek").Progress=.5;
            Field<IslandSeekBar>(window,"_expandedSeek").Progress=.5;

            var second=new MediaInfo {Title="Second track",Revision=2,Duration=TimeSpan.FromSeconds(180),IsPlaying=true};
            var secondLoad=(Task)Call(window,"UpdateMediaInfo",second)!;
            Assert(Field<List<LyricLine>?>(window,"_currentLyrics") is null,"Switch retained old lyric data");
            Assert(Field<TextBlock>(window,"_songTitle").Text==second.Title,"Expanded title waited for lyrics");
            Assert(Field<IslandSeekBar>(window,"_compactSeek").Progress==0
                && Field<IslandSeekBar>(window,"_expandedSeek").Progress==0,"A seek bar retained halfway progress");
            Assert(Field<StackPanel>(window,"_lyricsPanel").Children.OfType<TextBlock>().All(text=>text.Text!="First lyric"),
                "Expanded lyrics retained the preceding song during download");
            Assert(!new System.Windows.Documents.TextRange(Field<IslandCompactLyrics>(window,"_lyricText").ContentStart,
                Field<IslandCompactLyrics>(window,"_lyricText").ContentEnd).Text.Contains("First lyric"),
                "Compact lyrics retained the preceding song");
            Assert(Field<IslandPlaybackClock>(window,"_lyricClock").Read(0)==0,"Track switch retained old playback time");

            var third=new MediaInfo {Title="Third track",Revision=3,Duration=TimeSpan.FromSeconds(200),IsPlaying=true};
            var thirdLoad=(Task)Call(window,"UpdateMediaInfo",third)!;
            Assert(provider.Tokens[second.Title].IsCancellationRequested,"Old lyric request was not cancelled");
            provider.Complete(third.Title,"Third lyric");
            await thirdLoad;
            provider.Complete(second.Title,"Second lyric returned late");
            await secondLoad;
            Assert(Field<List<LyricLine>>(window,"_currentLyrics")[0].Text=="Third lyric","Late lyrics overwrote current song");
            await (Task)Call(window,"UpdateMediaInfo",first)!;
            Assert(Field<MediaInfo>(window,"_currentMedia").Revision==3,"An old metadata/artwork notification rolled back identity");

            var service=Field<MediaSessionService>(window,"_mediaSession");
            typeof(MediaSessionService).GetField("_timeline",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,
                new MediaSessionService.Timeline(TimeSpan.FromSeconds(120),TimeSpan.FromSeconds(240),true,false,1));
            Call(window,"OnUpdateTick",null,EventArgs.Empty);
            Assert(Field<IslandSeekBar>(window,"_compactSeek").Progress==0,"Previous timeline contaminated a new track");
            typeof(MediaSessionService).GetField("_timeline",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(service,
                new MediaSessionService.Timeline(TimeSpan.FromSeconds(50),third.Duration,true,true,3));
            Call(window,"OnUpdateTick",null,EventArgs.Empty);
            Assert(Math.Abs(Field<IslandSeekBar>(window,"_compactSeek").Progress-.25)<.001
                && Math.Abs(Field<IslandSeekBar>(window,"_expandedSeek").Progress-.25)<.001,"Views disagree on current progress");
            Assert(Field<StackPanel>(window,"_lyricsPanel").Children.OfType<TextBlock>().Any(text=>text.Text=="Third lyric"),
                "Expanded lyrics did not recover after a rapid switch");
            var other=new MediaInfo {Title=third.Title,SourceAppId="another-player",AlbumTitle="another-recording"};
            Assert(other.GetTrackKey()!=third.GetTrackKey(),"Different player recordings share a track identity");
            Assert(MediaSessionSelection.Rank("Tencent.QQMusic",true,false,true)>
                MediaSessionSelection.Rank("Weixin.exe",true,true,false),"A video session displaced background music");
            var guard=new MediaTimelineGuard();
            var stamp=DateTimeOffset.UtcNow;
            guard.Begin(120,240,stamp,stamp);
            Assert(!guard.Accept(120,240,stamp,stamp.AddMilliseconds(30)),"Old timeline stamp passed the track fence");
            Assert(!guard.Accept(120.05,240,stamp.AddMilliseconds(50),stamp.AddMilliseconds(50)),
                "The preceding song's advancing position passed the track fence");
            Assert(guard.Accept(.1,240,stamp.AddMilliseconds(100),stamp.AddMilliseconds(100)),
                "A genuine track reset did not release the timeline fence");
            Assert(guard.Accept(60,240,stamp.AddMilliseconds(200),stamp.AddMilliseconds(200)),
                "An ordinary external seek was blocked after synchronization");
            await CheckArtworkAsync(window,service,third);
            Console.WriteLine(JsonSerializer.Serialize(new {pass=true,checks=44,
                scenarios=new[] {"immediate reset of both views","halfway/rapid switches","cancelled requests",
                    "out-of-order lyrics/artwork","track-bound timelines","shared progress and lyric selection","independent island palette/persistence"}}));
        }
        finally {window.Close();}
    }

    private static void CheckPalette(IslandWindow window)
    {
        var previous=Program.Prefs;
        try {
            foreach(var color in Enum.GetValues<IslandAccentColor>()) {
                Program.Prefs=new Preferences {IslandLyricsColor=color,IslandProgressColor=color,IslandMeterColor=color};
                Call(window,"ApplyIslandColors");
                Assert(ReferenceEquals(Field<IslandSeekBar>(window,"_compactSeek").Accent,IslandPalette.Accent(color))
                    && ReferenceEquals(Field<IslandSeekBar>(window,"_expandedSeek").Accent,IslandPalette.Accent(color)),"Both progress colors");
                var compact=Field<IslandCompactLyrics>(window,"_lyricText");
                Assert(ReferenceEquals(typeof(IslandCompactLyrics).GetField("_current",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(compact),IslandPalette.Current(color)),"Compact lyric color");
                var expanded=Field<IslandLyricsView>(window,"_lyricsScroll");
                Assert(ReferenceEquals(typeof(IslandLyricsView).GetField("_activeBrush",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(expanded),IslandPalette.Accent(color)),"Expanded lyric color");
                Assert(Field<System.Windows.Shapes.Rectangle[]>(window,"_meterBars").All(bar=>ReferenceEquals(bar.Fill,IslandPalette.Meter(color))),"Audio meter color");
            }
            var independent=new Preferences {IslandLyricsColor=IslandAccentColor.BerryPink,
                IslandProgressColor=IslandAccentColor.KleinBlue,IslandMeterColor=IslandAccentColor.HermesOrange};
            var restored=JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(independent))!.Validated();
            Assert(restored.IslandLyricsColor==independent.IslandLyricsColor && restored.IslandProgressColor==independent.IslandProgressColor
                && restored.IslandMeterColor==independent.IslandMeterColor,"Independent palette persistence");
            Assert(JsonSerializer.Deserialize<Preferences>("{}")!.Validated().IslandLyricsColor==IslandAccentColor.Default,"Legacy settings retain default palette");
            Assert(new Preferences {IslandLyricsColor=(IslandAccentColor)999}.Validated().IslandLyricsColor==IslandAccentColor.Default,"Invalid palette fallback");
            Assert(IslandPalette.Options.All(option=>option.Swatch.IsFrozen),"Immutable shared palette brushes");
        }
        finally {Program.Prefs=previous;Call(window,"ApplyIslandColors");}
    }

    private static async Task CheckArtworkAsync(IslandWindow window,MediaSessionService service,MediaInfo current)
    {
        async Task<InMemoryRandomAccessStream> ImageStream(byte blue,byte red)
        {
            var image=BitmapSource.Create(1,1,96,96,PixelFormats.Bgra32,null,new byte[] {blue,0,red,255},4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using var output=new System.IO.MemoryStream();encoder.Save(output);
            var stream=new InMemoryRandomAccessStream();
            await stream.WriteAsync(output.ToArray().AsBuffer());
            stream.Seek(0);return stream;
        }
        using var oldImage=await ImageStream(0,255);
        using var newImage=await ImageStream(255,0);
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var mediaField=typeof(MediaSessionService).GetField("_media",flags)!;
        var lifetimeField=typeof(MediaSessionService).GetField("_coverLifetime",flags)!;
        var read=typeof(MediaSessionService).GetMethod("ReadCoverAsync",flags)!;
        MediaInfo? received=null;
        service.MediaChanged+=(_,media)=>received=media;
        mediaField.SetValue(service,current);
        var oldLifetime=new CancellationTokenSource();
        lifetimeField.SetValue(service,oldLifetime);
        await (Task)read.Invoke(service,new object[] {RandomAccessStreamReference.CreateFromStream(oldImage),
            new MediaInfo {Title="Outdated track",Revision=current.Revision-1},oldLifetime})!;
        Assert(received is null,"The preceding song's artwork was published");
        int clockRevision=Field<IslandPlaybackClock>(window,"_lyricClock").Revision;
        var lifetime=new CancellationTokenSource();
        lifetimeField.SetValue(service,lifetime);
        await (Task)read.Invoke(service,new object[] {RandomAccessStreamReference.CreateFromStream(newImage),current,lifetime})!;
        Assert(received?.Thumbnail is {Length:>0},"Late artwork for the current song was dropped");
        await (Task)Call(window,"UpdateMediaInfo",received)!;
        var compact=Field<Image>(window,"_albumCover").Source as BitmapSource;
        byte[] pixel=new byte[4];compact!.CopyPixels(pixel,4,0);
        Assert(pixel[0]==255 && pixel[2]==0 && ReferenceEquals(compact,Field<Image>(window,"_expandedCoverImage").Source)
            && Field<IslandPlaybackClock>(window,"_lyricClock").Revision==clockRevision,
            "Artwork did not update both views independently of the current playback clock");
    }

    private static async Task CheckLiveAsync()
    {
        using var media=new MediaSessionService();
        var window=new IslandWindow(new ImprovedLyricsService(),media);
        var coverTicks=new Dictionary<long,long>();
        media.MediaChanged+=(sender,info)=>window.Dispatcher.BeginInvoke(new Action(()=>
        {
            _=(Task)Call(window,"UpdateMediaInfo",info)!;
            if(info.Thumbnail is {Length:>0} && Field<Image>(window,"_albumCover").Source is not null)
                coverTicks.TryAdd(info.Revision,Stopwatch.GetTimestamp());
        }));
        Field<System.Windows.Threading.DispatcherTimer>(window,"_updateTimer").Start();
        Field<System.Windows.Threading.DispatcherTimer>(window,"_lyricTimer").Start();
        await media.InitializeAsync();
        MediaInfo? initial=null;
        await WaitAsync(async()=> {initial=await media.GetCurrentMediaInfoAsync();return initial is not null;},5000);
        Assert(initial is not null,"No live media session");
        await WaitAsync(()=>Task.FromResult(media.ReadTimeline()?.Revision==initial!.Revision),12000);
        var before=media.ReadTimeline();
        Assert(before is not null,"No initial live timeline");
        var started=Stopwatch.StartNew();
        long switchAt=Stopwatch.GetTimestamp();
        MediaInfo? next=null;
        try
        {
            await media.NextAsync();
            await WaitAsync(async()=> {next=await media.GetCurrentMediaInfoAsync();return next is not null && next.Revision>initial!.Revision;},5000);
            double identityMs=started.Elapsed.TotalMilliseconds;
            Assert(next is not null && next.Revision>initial!.Revision,"Background next did not update identity");
            await WaitAsync(()=>Task.FromResult(media.ReadTimeline()?.Revision==next!.Revision),12000);
            double firstProgressMs=started.Elapsed.TotalMilliseconds;
            await WaitAsync(()=>Task.FromResult(media.ReadTimeline() is {Estimated:false} actual && actual.Revision==next!.Revision),12000);
            var after=media.ReadTimeline();
            Assert(after is not null && after.Revision==next!.Revision,"New track did not acquire a matching timeline");
            Assert(!after!.Estimated,"Native progress was not reacquired");
            double timelineMs=started.Elapsed.TotalMilliseconds;
            bool matchingProgress=after.Position.TotalSeconds<=started.Elapsed.TotalSeconds+2;
            Call(window,"OnUpdateTick",null,EventArgs.Empty);
            await WaitAsync(()=>Task.FromResult(Field<Image>(window,"_albumCover").Source is not null),6000);
            bool cover=Field<Image>(window,"_albumCover").Source is not null
                && Field<Image>(window,"_expandedCoverImage").Source is not null;
            double coverMs=coverTicks.TryGetValue(next!.Revision,out long coverAt)
                ?(coverAt-switchAt)*1000d/Stopwatch.Frequency:started.Elapsed.TotalMilliseconds;
            Assert(cover,"A live player cover did not reach both views");
            Set(window,"_isExpanded",true);
            Call(window,"UpdateExpandedView");
            await WaitAsync(()=>Task.FromResult(Field<List<LyricLine>?>(window,"_currentLyrics") is {Count:>0}),10000);
            Call(window,"OnUpdateTick",null,EventArgs.Empty);
            bool lyrics=Field<List<LyricLine>?>(window,"_currentLyrics") is {Count:>0};
            await WaitAsync(()=>Task.FromResult(media.ReadTimeline()?.Playing==true),5000);
            Assert(media.ReadTimeline()?.Playing==true,"The selected track did not finish buffering");
            await media.TogglePlaybackAsync();
            await WaitAsync(()=>Task.FromResult(media.ReadTimeline()?.Playing==false),3000);
            Assert(media.ReadTimeline()?.Playing==false,"Pause was not synchronized");
            double paused=media.ReadTimeline()!.Position.TotalSeconds;
            await Task.Delay(150);
            Assert(Math.Abs(media.ReadTimeline()!.Position.TotalSeconds-paused)<.1,"Paused progress continued advancing");
            await media.TogglePlaybackAsync();
            await WaitAsync(()=>Task.FromResult(media.ReadTimeline()?.Playing==true),3000);
            Assert(media.ReadTimeline()?.Playing==true,"Resume was not synchronized");
            Console.WriteLine(JsonSerializer.Serialize(new {pass=matchingProgress,source=next!.SourceAppId,
                oldTitle=initial!.Title,newTitle=next.Title,identityMs,firstProgressMs,nativeTimelineMs=timelineMs,
                beforeSeconds=before!.Position.TotalSeconds,afterSeconds=after!.Position.TotalSeconds,
                cover,coverMs,lyrics,pauseResume=true,canSeek=after.CanSeek,raw=media.InspectTimelineSource()}));
            Assert(matchingProgress,"Timeline retained a previous-track position");
        }
        finally
        {
            await media.PreviousAsync();
            await WaitAsync(async()=> (await media.GetCurrentMediaInfoAsync())?.GetTrackKey()==initial!.GetTrackKey(),5000);
            if(before!.CanSeek)await media.SeekAsync(before.Position);
            window.Close();
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition,int milliseconds)
    {
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<milliseconds)
        {
            if(await condition())return;
            await Task.Delay(25);
        }
    }

    private sealed class DelayedProvider : ILyricsProvider
    {
        public string Name=>"Synchronization test";
        public int Priority=>100;
        private readonly Dictionary<string,TaskCompletionSource<List<LyricLine>?>> _requests=new();
        internal Dictionary<string,CancellationToken> Tokens {get;}=new();
        public Task<List<LyricLine>?> GetLyricsAsync(MediaInfo media,CancellationToken cancellationToken=default)
        {
            var request=new TaskCompletionSource<List<LyricLine>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests[media.Title]=request;
            Tokens[media.Title]=cancellationToken;
            return request.Task;
        }
        internal void Complete(string title,string text)
            =>_requests[title].SetResult(new List<LyricLine> {new() {Text=text,Time=TimeSpan.Zero}});
    }
}
