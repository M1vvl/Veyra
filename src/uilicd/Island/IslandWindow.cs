using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using GlassDock.Models;
using GlassDock.Music;

namespace GlassDock.Island;

/// <summary>
/// 使用 uilicd 光学材质的媒体灵动岛。
/// </summary>
internal class IslandWindow : GlassWindow
{
    protected override void Reposition() { }
    // Reuse one bounded buffer across both sizes, including rapid reversals.
    protected override Size MaterialAllocationSize=>new Size(532,272);
    private readonly DesktopNative.WinEventCallback _foregroundChanged;
    private nint _foregroundHook;
    private int _foregroundUpdateQueued;
    private bool _transitionRefreshAttached;
    private long _transitionRefreshUntil;
    private readonly DispatcherTimer _meterTimer=new() {Interval=TimeSpan.FromMilliseconds(60)};
    private readonly Rectangle[] _meterBars=new Rectangle[3];
    private readonly double[] _meterLevels=new double[3];
    private bool _readingMeter,_closed;
    private readonly IslandSeekBar _compactSeek=new(),_expandedSeek=new();
    private int _trackGeneration;
    private string? _lyricsTrack;
    private CancellationTokenSource? _lyricsRequest;
    private bool _lyricsRequestActive;
    private long _lyricsRetryAfter;
    private LyricLine? _highlightedLine;
    private readonly MediaSessionService _mediaSession;
    private readonly ImprovedLyricsService _lyricsService;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _lyricTimer;

    private MediaInfo? _currentMedia;
    private List<LyricLine>? _currentLyrics;
    private bool _isExpanded;
    private bool _isDragging;
    private Point _dragStartPoint;
    private readonly IslandPlaybackClock _lyricClock=new();
    private bool _seekTimelinePending;
    private static double MonotonicSeconds=>System.Diagnostics.Stopwatch.GetTimestamp()
        /(double)System.Diagnostics.Stopwatch.Frequency;

    // UI 元素 - 紧凑模式
    private readonly Grid _rootGrid;
    private readonly Border _islandBorder;
    private readonly Grid _compactHeader;
    private readonly Border _albumCoverBorder;
    private readonly Image _albumCover;
    private readonly IslandCompactLyrics _lyricText;
    private readonly Border _lyricViewport;
    private readonly StackPanel _controlButtons;
    private readonly Button _prevButton;
    private readonly Button _playPauseButton;
    private readonly Button _nextButton;

    // UI 元素 - 展开模式
    private readonly Grid _expandedContent;
    private readonly Border _expandedAlbumCover;
    private readonly Image _expandedCoverImage;
    private readonly TextBlock _songTitle;
    private readonly TextBlock _artistName;
    private readonly IslandLyricsView _lyricsScroll;
    private readonly StackPanel _lyricsPanel;
    private readonly Slider _progressSlider;
    private readonly TextBlock _currentTime;
    private readonly TextBlock _totalTime;

    // 旋转动画
    private readonly ScaleTransform _coverScale=new(1,1);
    private readonly TranslateTransform _coverPlacement=new();
    private readonly RectangleGeometry _contentClip=new();
    private static readonly DependencyProperty ExpansionProperty=DependencyProperty.Register(
        "Expansion",typeof(double),typeof(IslandWindow),
        new PropertyMetadata(0d,(owner,_)=>((IslandWindow)owner).ApplyExpansion()));
    private double Expansion {get=>(double)GetValue(ExpansionProperty);set=>SetValue(ExpansionProperty,value);}
    private int _transitionRevision;
    private bool _seeking;
    private string? _seekError;
    private List<LyricLine>? _renderedLyrics;
    private bool _lyricsRealized;
    private double? _pendingSeekFraction;
    private readonly RotateTransform _coverRotation=new();
    private readonly AnimationClock _rotationClock;
    private bool _rotationPlaying;
    private bool? _displayedPlayback;
    private int _displayedSecond=-1,_displayedDuration=-1;

    public IslandWindow() : this(new ImprovedLyricsService()) { }

    internal IslandWindow(ImprovedLyricsService lyricsService,MediaSessionService? mediaSession=null) : base("uilicd · 灵动岛屿")
    {
        _foregroundChanged=(_,_,_,_,_,_,_)=>QueueForegroundRefresh();
        _mediaSession = mediaSession??new MediaSessionService();
        _lyricsService = lyricsService;

        // 窗口设置
        // Keep the layered HWND stable. Only its visible glass surface changes size.
        Width = 532;
        Height = 272;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // 定位到屏幕顶部中央
        WindowStartupLocation = WindowStartupLocation.Manual;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        Left = (screenWidth - Width) / 2;
        Top = 8;

        // Sonara's record artwork uses a continuous 12-second linear rotation.
        var rotation=new DoubleAnimation(0,360,TimeSpan.FromSeconds(12))
        {
            RepeatBehavior=RepeatBehavior.Forever
        };
        _rotationClock=(AnimationClock)rotation.CreateClock(true);
        _coverRotation.ApplyAnimationClock(RotateTransform.AngleProperty,_rotationClock);
        _rotationClock.Controller!.Begin();
        _rotationClock.Controller.Pause();

        // 创建 UI
        _rootGrid = new Grid();
        ContentLayer.Children.Add(_rootGrid);
        ContentLayer.Clip=_contentClip;
        SizeChanged+=OnIslandSizeChanged;

        _islandBorder = CreateIslandBorder();
        _rootGrid.Children.Add(_islandBorder);

        // 创建紧凑模式内容
        _compactHeader = new Grid { Height=42,VerticalAlignment=VerticalAlignment.Top };
        var meters=new StackPanel {Orientation=Orientation.Horizontal,Width=20,Height=30,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,0,0)};
        for(int index=0;index<3;index++)
        {
            var bar=new Rectangle {Width=2,Height=4,RadiusX=1,RadiusY=1,Margin=new Thickness(0,0,3,0),Fill=new SolidColorBrush(Color.FromRgb(224,255,155)),VerticalAlignment=VerticalAlignment.Center};
            _meterBars[index]=bar; meters.Children.Add(bar);
        }
        _compactHeader.Children.Add(meters);
        _meterTimer.Tick+=async(_,_)=>
        {
            if(_readingMeter || _closed)return;
            _readingMeter=true;
            try
            {
                float peak=await Task.Run(Services.AudioService.ReadOutputPeak);
                if(_closed)return;
                for(int index=0;index<3;index++)
                {
                    double target=_currentMedia?.IsPlaying==true?Math.Sqrt(peak):0;
                    _meterLevels[index]+=(target-_meterLevels[index])*(index==0?.8:index==1?.55:.35);
                    double height=4+Math.Min(1,_meterLevels[index])*16;
                    _meterBars[index].BeginAnimation(HeightProperty,new DoubleAnimation(height,TimeSpan.FromMilliseconds(65)));
                }
            }
            catch(Exception ex) {Program.Log("Island meter: "+ex.Message);}
            finally {_readingMeter=false;}
        };
        _meterTimer.Start();
        _islandBorder.Child = _compactHeader;

        // 左侧：专辑封面
        _albumCoverBorder = new Border
        {
            Width = 28,
            Height = 28,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(32, 0, 0, 0),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Clip = new EllipseGeometry(new Point(14,14),14,14),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        _albumCover = new Image
        {
            Stretch = Stretch.UniformToFill
        };
        _albumCoverBorder.Child = _albumCover;

        _albumCover.RenderTransformOrigin=new Point(.5,.5);
        _albumCover.RenderTransform=_coverRotation;
        _albumCoverBorder.RenderTransformOrigin=new Point(0,0);
        var coverLayout=new TransformGroup();
        coverLayout.Children.Add(_coverScale);
        coverLayout.Children.Add(_coverPlacement);
        _albumCoverBorder.RenderTransform=coverLayout;

        _compactHeader.Children.Add(_albumCoverBorder);

        // 中间：歌词文本
        _lyricText = new IslandCompactLyrics();
        _lyricText.ShowTitle("等待播放...");
        _lyricViewport=new Border {Margin=new Thickness(80,0,100,0),ClipToBounds=true,Child=_lyricText};
        _compactHeader.Children.Add(_lyricViewport);

        // 右侧：控制按钮
        _controlButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 11, 0)
        };

        _prevButton = CreateControlButton("M7 5v14M19 5 9 12l10 7Z", "上一首");
        _playPauseButton = CreateControlButton("m8 5 11 7-11 7Z", "播放");
        _nextButton = CreateControlButton("M17 5v14M5 5l10 7-10 7Z", "下一首");

        _prevButton.Click += (s, e) => OnPreviousClick();
        _playPauseButton.Click += (s, e) => OnPlayPauseClick();
        _nextButton.Click += (s, e) => OnNextClick();

        _controlButtons.Children.Add(_prevButton);
        _controlButtons.Children.Add(_playPauseButton);
        _controlButtons.Children.Add(_nextButton);

        _compactHeader.Children.Add(_controlButtons);

        // 创建展开模式内容（初始隐藏）
        _expandedContent = new Grid { Visibility = Visibility.Collapsed,Opacity=0 };
        _expandedContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        _expandedContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧：大封面
        var leftPanel = new Grid
        {
            Margin = new Thickness(16, 16, 0, 16)
        };
        _expandedAlbumCover = new Border
        {
            Width = 110,
            Height = 110,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top
        };
        _expandedCoverImage = new Image
        {
            Stretch = Stretch.UniformToFill
        };
        _expandedAlbumCover.Child = _expandedCoverImage;
        _expandedAlbumCover.Visibility=Visibility.Hidden;
        leftPanel.Children.Add(_expandedAlbumCover);
        Grid.SetColumn(leftPanel, 0);
        _expandedContent.Children.Add(leftPanel);

        // 右侧：歌词区域
        var rightPanel = new Grid
        {
            Margin = new Thickness(16, 55, 20, 29)
        };
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 歌曲信息
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _songTitle = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _artistName = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(178, 255, 255, 255)),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        headerStack.Children.Add(_songTitle);
        headerStack.Children.Add(_artistName);
        Grid.SetRow(headerStack, 0);
        rightPanel.Children.Add(headerStack);

        // 歌词列表
        _lyricsScroll = new IslandLyricsView();
        _lyricsPanel = _lyricsScroll.Lines;
        Grid.SetRow(_lyricsScroll, 1);
        rightPanel.Children.Add(_lyricsScroll);

        // 进度条和时间
        var progressGrid = new Grid();
        progressGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        progressGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _progressSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Height = 10,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _progressSlider.IsEnabled = false; // Seeking is not yet connected to the media session.
        _progressSlider.PreviewMouseDown += (s, e) => _isDragging = true;
        _progressSlider.PreviewMouseUp += (s, e) =>
        {
            _isDragging = false;
            if (_currentMedia != null && _currentMedia.Duration.TotalSeconds > 0)
            {
                var newPosition = TimeSpan.FromSeconds(_progressSlider.Value);
                // 这里需要通过 MediaSession API 设置播放位置（暂不支持）
            }
        };
        Grid.SetRow(_progressSlider, 0);
        progressGrid.Children.Add(_expandedSeek);

        var timePanel = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        timePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        timePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _currentTime = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(183, 255, 255, 255)),
            FontSize = 9,
            Text = "0:00"
        };
        _totalTime = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(183, 255, 255, 255)),
            FontSize = 9,
            HorizontalAlignment = HorizontalAlignment.Right,
            Text = "0:00"
        };
        Grid.SetColumn(_currentTime, 0);
        Grid.SetColumn(_totalTime, 1);
        timePanel.Children.Add(_currentTime);
        timePanel.Children.Add(_totalTime);
        Grid.SetRow(timePanel, 1);
        progressGrid.Children.Add(timePanel);

        Grid.SetRow(progressGrid, 2);
        rightPanel.Children.Add(progressGrid);

        Grid.SetColumn(rightPanel, 1);
        _expandedContent.Children.Add(rightPanel);

        _rootGrid.Children.Add(_expandedContent);
        _compactSeek.VerticalAlignment=VerticalAlignment.Bottom;
        _compactSeek.Margin=new Thickness(16,0,16,0);
        _rootGrid.Children.Add(_compactSeek);
        _expandedSeek.Visibility=Visibility.Collapsed;
        _compactSeek.SeekRequested+=Seek;
        _expandedSeek.SeekRequested+=Seek;

        // 事件处理
        _islandBorder.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _islandBorder.MouseLeftButtonUp += OnMouseLeftButtonUp;
        _islandBorder.MouseMove += OnMouseMove;
        MouseDoubleClick += OnDoubleClick;

        // 定时器
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        _updateTimer.Tick += OnUpdateTick;

        _lyricTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(12.5)
        };
        _lyricTimer.Tick += OnLyricTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
        ApplyIslandColors();
    }

    public override void ApplyPreferences()
    {
        base.ApplyPreferences();
        ApplyIslandColors();
    }

    private void ApplyIslandColors()
    {
        _lyricText.SetAccent(Program.Prefs.IslandLyricsColor);
        _lyricsScroll.SetAccent(Program.Prefs.IslandLyricsColor);
        _compactSeek.SetAccent(Program.Prefs.IslandProgressColor);
        _expandedSeek.SetAccent(Program.Prefs.IslandProgressColor);
        var meter=IslandPalette.Meter(Program.Prefs.IslandMeterColor);
        foreach(var bar in _meterBars)bar.Fill=meter;
    }

    private Border CreateIslandBorder()
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };

        return border;
    }

    private Button CreateControlButton(string pathData, string tooltip)
    {
        var button = new Button
        {
            Width = 25,
            Height = 25,
            Margin = new Thickness(1.5),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };

        // Own the complete template so Windows theme chrome cannot paint blue hover/focus fills.
        button.OverridesDefaultStyle=true;
        button.FocusVisualStyle=null;
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);
        var hitSurface=new FrameworkElementFactory(typeof(Border));
        hitSurface.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        hitSurface.AppendChild(presenter);
        button.Template=new ControlTemplate(typeof(Button)) {VisualTree=hitSurface};

        var canvas = new Canvas
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var path = new Path
        {
            Data = Geometry.Parse(pathData),
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        canvas.Children.Add(path);
        button.Content = new Viewbox {Width=17,Height=17,Child=canvas};

        return button;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _foregroundHook=DesktopNative.WatchForeground(_foregroundChanged);
            _mediaSession.MediaChanged += OnMediaChanged;
            _mediaSession.PlaybackStatusChanged += OnPlaybackStatusChanged;
            await _mediaSession.InitializeAsync();
            if(_closed)return;

            _updateTimer.Start();
            _lyricTimer.Start();

            // 立即获取当前播放信息
            var media = await _mediaSession.GetCurrentMediaInfoAsync();
            if (media != null)
            {
                await UpdateMediaInfo(media);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"灵动岛屿初始化失败: {ex.Message}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed=true; _transitionRevision++; _trackGeneration++;
        if(_foregroundHook!=0) {DesktopNative.UnhookWinEvent(_foregroundHook); _foregroundHook=0;}
        StopTransitionRefresh();
        BeginAnimation(ExpansionProperty,null);
        _mediaSession.MediaChanged-=OnMediaChanged;
        _mediaSession.PlaybackStatusChanged-=OnPlaybackStatusChanged;
        _meterTimer.Stop();
        _updateTimer.Stop();
        _lyricTimer.Stop();
        _rotationClock.Controller?.Remove();
        _coverRotation.ApplyAnimationClock(RotateTransform.AngleProperty,null);
        _mediaSession.Dispose();
        _lyricsRequest?.Cancel();
        _lyricsService.Dispose();
        _expandedContent.BeginAnimation(OpacityProperty,null);
        _lyricText.BeginAnimation(OpacityProperty,null);
        _compactSeek.BeginAnimation(MarginProperty,null);
        _albumCover.Source=null;
        _expandedCoverImage.Source=null;
        _lyricsScroll.SelectLine(null);
        _currentMedia=null; _currentLyrics=null; _renderedLyrics=null;
        _lyricsPanel.Children.Clear();
    }

    private void QueueForegroundRefresh()
    {
        if(_closed || Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref _foregroundUpdateQueued,1)!=0)return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=> {
            Interlocked.Exchange(ref _foregroundUpdateQueued,0);
            if(_closed || !IsVisible)return;
            Rendering.SharedDesktopCapture.InvalidatePresentation();
            _transitionRefreshUntil=Environment.TickCount64+180;
            if(!_transitionRefreshAttached)
            {
                _transitionRefreshAttached=true;
                CompositionTarget.Rendering+=RefreshTransitionFrame;
            }
            RenderGlass();
        }));
    }

    private void RefreshTransitionFrame(object? sender,EventArgs args)
    {
        if(_closed || !IsVisible || Environment.TickCount64>_transitionRefreshUntil)
        {
            StopTransitionRefresh();
            return;
        }
        RenderGlass();
    }

    private void StopTransitionRefresh()
    {
        if(!_transitionRefreshAttached)return;
        CompositionTarget.Rendering-=RefreshTransitionFrame;
        _transitionRefreshAttached=false;
    }

    private async void OnMediaChanged(object? sender, MediaInfo e)
    {
        if(_closed || Dispatcher.HasShutdownStarted)return;
        try
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if(!_closed)await UpdateMediaInfo(e);
            }).Task.Unwrap();
        }
        catch(TaskCanceledException) when(_closed || Dispatcher.HasShutdownStarted) { }
    }

    private void OnPlaybackStatusChanged(object? sender, EventArgs e)
    {
        if(_closed || Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(new Action(()=>OnUpdateTick(null,EventArgs.Empty)));
    }

    private async Task UpdateMediaInfo(MediaInfo media)
    {
        if(_closed || _currentMedia is not null && media.Revision<_currentMedia.Revision)return;
        bool sameTrack=_currentMedia is not null && _currentMedia.Revision==media.Revision
            && _currentMedia.SourceAppId==media.SourceAppId;
        if(sameTrack)
        {
            media.Position=_currentMedia!.Position;
            if(media.Duration<=TimeSpan.Zero)media.Duration=_currentMedia.Duration;
        }
        _currentMedia = media;
        if(!sameTrack)
        {
            _albumCover.Source=null;
            _expandedCoverImage.Source=null;
        }

        // 更新封面
        if (media.Thumbnail != null && media.Thumbnail.Length > 0)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(media.Thumbnail);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                if(media.SourceAppId.Contains("qqmusic",StringComparison.OrdinalIgnoreCase))bitmap.DecodePixelWidth=300;
                bitmap.StreamSource = ms;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                _albumCover.Source = bitmap;
                _expandedCoverImage.Source = bitmap;
            }
            catch { }
        }

        // 更新播放/暂停按钮
        UpdatePlayPauseButton(media.IsPlaying);

        // 更新旋转动画
        UpdateRotation(media.IsPlaying);

        string key=media.GetTrackKey();
        if(sameTrack && _lyricsTrack==key)return;
        if(sameTrack)
        {
            _lyricsTrack=key;
            _lyricsRequest?.Cancel();
            _currentLyrics=null;
            _lyricText.ShowTitle(media.Title);
            if(_isExpanded)UpdateExpandedView();
            await LoadLyricsAsync(media,++_trackGeneration);
            return;
        }
        _lyricsTrack=key;
        _lyricsRequest?.Cancel();
        _lyricClock.Reset();
        _seekTimelinePending=false;
        _pendingSeekFraction=null;
        _seekError=null;
        int generation=++_trackGeneration;
        _currentLyrics=null;
        _highlightedLine=null;
        _lyricsRetryAfter=long.MaxValue;
        _lyricsRealized=false;
        _compactSeek.Progress=_expandedSeek.Progress=0;
        _compactSeek.IsEnabled=_expandedSeek.IsEnabled=false;
        _currentTime.Text="0:00";
        _totalTime.Text=FormatTime(media.Duration);
        _displayedSecond=_displayedDuration=-1;
        _lyricText.ShowTitle(string.IsNullOrEmpty(media.Title)?"等待播放...":media.Title);
        if(_isExpanded)UpdateExpandedView();
        OnUpdateTick(null,EventArgs.Empty);
        await LoadLyricsAsync(media,generation);
    }

    private async Task LoadLyricsAsync(MediaInfo media,int generation)
    {
        using var request=new CancellationTokenSource();
        _lyricsRequest=request;
        _lyricsRequestActive=true;
        try
        {
            var lyrics=await _lyricsService.GetLyricsAsync(media,request.Token);
            if(_closed || request.IsCancellationRequested || generation!=_trackGeneration)return;
            _currentLyrics=lyrics?.OrderBy(line=>line.Time).ToList();
            _lyricsRetryAfter=Environment.TickCount64+5000;
            if(_isExpanded)UpdateExpandedView();
            OnLyricTick(null,EventArgs.Empty);
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) {Program.Log("Island lyrics: "+ex.Message);}
        finally
        {
            if(ReferenceEquals(_lyricsRequest,request))
            {
                _lyricsRequest=null;
                _lyricsRequestActive=false;
            }
        }
    }

    private void UpdatePlayPauseButton(bool isPlaying)
    {
        if(_displayedPlayback==isPlaying)return;
        _displayedPlayback=isPlaying;
        _updateTimer.Interval=TimeSpan.FromMilliseconds(isPlaying?25:200);
        if(isPlaying)_lyricTimer.Start();else _lyricTimer.Stop();
        var path = (_playPauseButton.Content as Viewbox)?.Child as Canvas;
        if (path?.Children[0] is Path iconPath)
        {
            iconPath.Data = isPlaying
                ? Geometry.Parse("M9 5v14M15 5v14") // 暂停图标
                : Geometry.Parse("m8 5 11 7-11 7Z"); // 播放图标
        }
        _playPauseButton.ToolTip = isPlaying ? "暂停" : "播放";
    }

    private void UpdateRotation(bool isPlaying)
    {
        if(_closed || _rotationPlaying==isPlaying)return;
        _rotationPlaying=isPlaying;
        if(isPlaying)_rotationClock.Controller?.Resume();
        else _rotationClock.Controller?.Pause();
    }

    private async void Seek(double fraction)
    {
        _seekError=null;
        _pendingSeekFraction=Math.Clamp(fraction,0,1);
        if(_seeking)return;
        _seeking=true;
        try
        {
            while(!_closed && _pendingSeekFraction is double latest && _currentMedia is { } media)
            {
                _pendingSeekFraction=null;
                var target=TimeSpan.FromSeconds(media.Duration.TotalSeconds*latest);
                bool changed=await _mediaSession.SeekAsync(target);
                if(_closed)return;
                if(!ReferenceEquals(media,_currentMedia))return;
                if(!changed)
                {
                    _seekError="播放器未执行跳转";
                    _compactSeek.ToolTip=_expandedSeek.ToolTip=_seekError;
                    _pendingSeekFraction=null;
                    break;
                }
                _seekTimelinePending=true;
                OnUpdateTick(null,EventArgs.Empty);
            }
        }
        catch(Exception ex) {Program.Log("Island seek: "+ex.Message);}
        finally {_seeking=false; if(!_closed)OnUpdateTick(null,EventArgs.Empty);}
    }

    private void OnUpdateTick(object? sender, EventArgs e)
    {
        if(_closed)return;
        if(_currentMedia is { } current && !string.IsNullOrEmpty(current.Title) && _currentLyrics is null
            && !_lyricsRequestActive && Environment.TickCount64>=_lyricsRetryAfter)
            _=LoadLyricsAsync(current,_trackGeneration);
        var timeline=_mediaSession.ReadTimeline();
        if(_currentMedia is null || timeline is null || timeline.Revision!=_currentMedia.Revision)return;
        double now=MonotonicSeconds;
        double observedPosition=timeline.Position.TotalSeconds;
        if(timeline.Playing)observedPosition+=Math.Max(0,Environment.TickCount64-timeline.ObservedAt)/1000d;
        _lyricClock.Observe(observedPosition,timeline.Playing,now,_seekTimelinePending);
        _seekTimelinePending=false;
        _currentMedia.Position=TimeSpan.FromSeconds(_lyricClock.Read(now));
        _currentMedia.Duration=timeline.Duration;
        _currentMedia.IsPlaying=timeline.Playing;
        UpdatePlayPauseButton(_currentMedia.IsPlaying);
        UpdateRotation(_currentMedia.IsPlaying);
        double duration=_currentMedia.Duration.TotalSeconds;
        UpdateSeekBar(_compactSeek,timeline,duration);
        UpdateSeekBar(_expandedSeek,timeline,duration);
        int second=(int)_currentMedia.Position.TotalSeconds;
        int durationSecond=(int)_currentMedia.Duration.TotalSeconds;
        if(_displayedSecond!=second) {_displayedSecond=second; _currentTime.Text=FormatTime(_currentMedia.Position);}
        if(_displayedDuration!=durationSecond) {_displayedDuration=durationSecond; _totalTime.Text=FormatTime(_currentMedia.Duration);}
        OnLyricTick(null,EventArgs.Empty);
    }

    private void UpdateSeekBar(IslandSeekBar seek,MediaSessionService.Timeline timeline,double duration)
    {
        seek.IsEnabled=timeline.CanSeek && duration>0;
        seek.ToolTip=_seekError??(duration>0?(timeline.CanSeek?"播放进度":"播放器不支持跳转"):"播放器未提供实时进度");
        if(!seek.Dragging && !_seeking)seek.Progress=duration>0?_currentMedia!.Position.TotalSeconds/duration:0;
        seek.InvalidateVisual();
    }

    private void OnLyricTick(object? sender, EventArgs e)
    {
        if(_closed)return;
        double position=_lyricClock.Read(MonotonicSeconds);
        if (_currentMedia == null || _currentLyrics == null || _currentLyrics.Count == 0)
        {
            _lyricText.ShowTitle(_currentMedia?.Title??"等待播放...");
            return;
        }
        var currentPosition=TimeSpan.FromSeconds(position);
        int low=0,high=_currentLyrics.Count-1,index=-1;
        while(low<=high)
        {
            int middle=low+(high-low)/2;
            if(_currentLyrics[middle].Time<=currentPosition) {index=middle; low=middle+1;}
            else high=middle-1;
        }
        if(index<0)
        {
            _lyricText.ShowTitle(_currentMedia.Title);
            if(_highlightedLine is not null) {_highlightedLine=null;_lyricsScroll.SelectLine(null);}
            return;
        }
        var currentLine=_currentLyrics[index];
        var nextLine=index+1<_currentLyrics.Count?_currentLyrics[index+1]:null;
        double end=nextLine?.Time.TotalSeconds??Math.Max(currentLine.Time.TotalSeconds+6,_currentMedia.Duration.TotalSeconds);
        _lyricText.ShowLine(currentLine,end,position);
        if (_isExpanded && _highlightedLine!=currentLine)
        {
            _highlightedLine=currentLine;
            HighlightCurrentLyric(currentLine);
        }
    }

    private void HighlightCurrentLyric(LyricLine currentLine)
    {
        _lyricsScroll.SelectLine(_lyricsPanel.Children.OfType<TextBlock>()
            .FirstOrDefault(child=>ReferenceEquals(child.Tag,currentLine)));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount>1 || IsInteractiveSource(e.OriginalSource as DependencyObject))
            return;

        _isDragging = true;
        _dragStartPoint = e.GetPosition(this);
        _islandBorder.CaptureMouse();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            _islandBorder.ReleaseMouseCapture();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var currentPosition = e.GetPosition(this);
            var offset = currentPosition - _dragStartPoint;

            // 只允许横向移动
            Left += offset.X;

            // 保持在屏幕顶部
            Top = 8;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveSource(e.OriginalSource as DependencyObject))
            return;

        ToggleExpanded();
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while(source is Visual)
        {
            if(source is System.Windows.Controls.Primitives.ButtonBase or IslandSeekBar or Slider)return true;
            source=VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ToggleExpanded()
    {
        if(_closed)return;
        _isDragging=false;
        if(_islandBorder.IsMouseCaptured)_islandBorder.ReleaseMouseCapture();
        _isExpanded=!_isExpanded;
        int revision=++_transitionRevision;
        double target=_isExpanded?1:0;
        double current=Expansion;
        _expandedContent.Visibility=Visibility.Visible;
        _expandedContent.IsHitTestVisible=_isExpanded;
        if(_isExpanded)UpdateExpandedView();
        // One clock drives geometry, artwork and content, including mid-flight reversals.
        var animation=new DoubleAnimation(current,target,TimeSpan.FromMilliseconds(320))
        {
            EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}
        };
        animation.Completed+=(_,_)=> {
            if(_closed || revision!=_transitionRevision)return;
            BeginAnimation(ExpansionProperty,null);
            Expansion=target;
            ApplyExpansion();
            if(!_isExpanded) {_expandedContent.Visibility=Visibility.Collapsed; OnLyricTick(null,EventArgs.Empty);}
        };
        BeginAnimation(ExpansionProperty,animation,HandoffBehavior.SnapshotAndReplace);
    }

    private void OnIslandSizeChanged(object sender,SizeChangedEventArgs e)=>ApplyExpansion();

    private void ApplyExpansion()
    {
        if(_rootGrid is null || _expandedContent is null)return;
        double progress=Math.Clamp(Expansion,0,1);
        double width=480+40*progress;
        double height=44+216*progress;
        double radius=22+4*progress;
        SetSurface(new Rect((532-width)/2,6,width,height),radius);
        _contentClip.Rect=new Rect(2,2,width-4,height-4);
        _contentClip.RadiusX=_contentClip.RadiusY=radius-2;
        _coverScale.ScaleX=_coverScale.ScaleY=1+2.8*progress;
        _coverPlacement.X=-11*progress;
        _coverPlacement.Y=47*progress;
        _lyricText.Opacity=1-progress;
        _expandedContent.Opacity=progress;
        _compactSeek.Margin=new Thickness(16+130*progress,0,16+4*progress,16*progress);
    }

    protected override nint WndProc(nint hwnd,int msg,nint wParam,nint lParam,ref bool handled)
    {
        if(msg==0x0084)
        {
            var point=PointFromScreen(new Point((short)((long)lParam&0xffff),(short)(((long)lParam>>16)&0xffff)));
            if(!Surface.Contains(point)) {handled=true;return -1;}
        }
        return base.WndProc(hwnd,msg,wParam,lParam,ref handled);
    }

    private void UpdateExpandedView()
    {
        if (_currentMedia == null)
            return;

        _songTitle.Text = _currentMedia.Title;
        _artistName.Text = _currentMedia.Artist;

        if(_lyricsRealized && ReferenceEquals(_renderedLyrics,_currentLyrics))return;
        _lyricsRealized=true;
        _renderedLyrics=_currentLyrics;
        _lyricsScroll.SelectLine(null);
        _highlightedLine=null;
        _lyricsPanel.Children.Clear();

        if (_currentLyrics != null && _currentLyrics.Count > 0)
        {
            foreach (var line in _currentLyrics)
            {
                var textBlock = new TextBlock
                {
                    Text = line.Text,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    Margin = new Thickness(0, 2.5, 0, 2.5),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Opacity = 0.45,
                    Tag = line
                };
                _lyricsPanel.Children.Add(textBlock);
            }
        }
        else
        {
            var noLyrics = new TextBlock
            {
                Text = "暂无歌词",
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            };
            _lyricsPanel.Children.Add(noLyrics);
        }
    }

    private async void OnPreviousClick()
    {
        await _mediaSession.PreviousAsync();
    }

    private async void OnPlayPauseClick()
    {
        await _mediaSession.TogglePlaybackAsync();
    }

    private async void OnNextClick()
    {
        await _mediaSession.NextAsync();
    }

    private string FormatTime(TimeSpan time)
    {
        return $"{(int)time.TotalMinutes}:{time.Seconds:D2}";
    }
}
