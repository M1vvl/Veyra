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
/// 使用 Veyra 光学材质的媒体灵动岛。
/// </summary>
internal class IslandWindow : GlassWindow
{
    protected override void Reposition() { }
    // Reuse one bounded buffer across both sizes, including rapid reversals.
    protected override Size MaterialAllocationSize=>new Size(532,272);
    private readonly DispatcherTimer _meterTimer=new() {Interval=TimeSpan.FromMilliseconds(60)};
    private readonly Rectangle[] _meterBars=new Rectangle[3];
    private readonly double[] _meterLevels=new double[3];
    private bool _readingMeter,_closed;
    private readonly IslandSeekBar _compactSeek=new(),_expandedSeek=new();
    private int _trackGeneration;
    private string? _lyricsTrack;
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
    private DateTime _positionUpdateTime;

    // UI 元素 - 紧凑模式
    private readonly Grid _rootGrid;
    private readonly Border _islandBorder;
    private readonly Grid _compactHeader;
    private readonly Border _albumCoverBorder;
    private readonly Image _albumCover;
    private readonly TextBlock _lyricText;
    private readonly Border _lyricViewport;
    private readonly TranslateTransform _lyricScroll=new();
    private AnimationClock? _lyricScrollClock;
    private double _lyricScrollSeconds=6;
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
    private readonly ScrollViewer _lyricsScroll;
    private readonly StackPanel _lyricsPanel;
    private readonly Slider _progressSlider;
    private readonly TextBlock _currentTime;
    private readonly TextBlock _totalTime;

    // 旋转动画
    private readonly ScaleTransform _coverScale=new(1,1);
    private readonly TranslateTransform _coverPlacement=new();
    private readonly RectangleGeometry _contentClip=new();
    private int _transitionRevision;
    private bool _transitioning;
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

    public IslandWindow() : base("Veyra · 灵动岛屿")
    {
        _mediaSession = new MediaSessionService();
        _lyricsService = new ImprovedLyricsService();
        _positionUpdateTime = DateTime.Now;

        // 窗口设置
        Width = 492;
        Height = 56;
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
        _lyricText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(209, 250, 107)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap,
            RenderTransform=_lyricScroll,
            Text = "等待播放..."
        };
        _lyricViewport=new Border {Margin=new Thickness(80,0,100,0),ClipToBounds=true,Child=_lyricText};
        _lyricViewport.SizeChanged+=(_,_)=> {if(!_isExpanded && !_transitioning)StartLyricScroll();};
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
        _lyricsScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _lyricsPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        _lyricsScroll.Content = _lyricsPanel;
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
            await _mediaSession.InitializeAsync();
            if(_closed)return;
            _mediaSession.MediaChanged += OnMediaChanged;
            _mediaSession.PlaybackStatusChanged += OnPlaybackStatusChanged;

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
        _closed=true; _transitioning=false; _transitionRevision++; _trackGeneration++;
        BeginAnimation(WidthProperty,null);
        BeginAnimation(HeightProperty,null);
        BeginAnimation(LeftProperty,null);
        _mediaSession.MediaChanged-=OnMediaChanged;
        _mediaSession.PlaybackStatusChanged-=OnPlaybackStatusChanged;
        _meterTimer.Stop();
        _updateTimer.Stop();
        _lyricTimer.Stop();
        _lyricScrollClock?.Controller?.Remove();
        _rotationClock.Controller?.Remove();
        _coverRotation.ApplyAnimationClock(RotateTransform.AngleProperty,null);
        _mediaSession.Dispose();
        _lyricsService.Dispose();
        _lyricScroll.ApplyAnimationClock(TranslateTransform.XProperty,null);
        _lyricScrollClock=null;
        _expandedContent.BeginAnimation(OpacityProperty,null);
        _lyricText.BeginAnimation(OpacityProperty,null);
        _compactSeek.BeginAnimation(MarginProperty,null);
        _albumCover.Source=null;
        _expandedCoverImage.Source=null;
        _currentMedia=null; _currentLyrics=null; _renderedLyrics=null;
        _lyricsPanel.Children.Clear();
    }

    private async void OnMediaChanged(object? sender, MediaInfo e)
    {
        if(_closed || Dispatcher.HasShutdownStarted)return;
        await Dispatcher.InvokeAsync(async () =>
        {
            if(!_closed)await UpdateMediaInfo(e);
        }).Task.Unwrap();
    }

    private void OnPlaybackStatusChanged(object? sender, EventArgs e)
    {
        if(_closed || Dispatcher.HasShutdownStarted)return;
        Dispatcher.BeginInvoke(new Action(()=>OnUpdateTick(null,EventArgs.Empty)));
    }

    private async Task UpdateMediaInfo(MediaInfo media)
    {
        if(_closed)return;
        _currentMedia = media;
        _positionUpdateTime = DateTime.Now;

        // 更新封面
        if (media.Thumbnail != null && media.Thumbnail.Length > 0)
        {
            try
            {
                using var ms = new System.IO.MemoryStream(media.Thumbnail);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
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
        if(_lyricsTrack==key)return;
        _lyricsTrack=key;
        int generation=++_trackGeneration;
        _currentLyrics=null;
        _highlightedLine=null;
        SetLyricText(media.Title);
        var lyrics=await _lyricsService.GetLyricsAsync(media);
        if(_closed || generation!=_trackGeneration)return;
        _currentLyrics=lyrics?.OrderBy(line=>line.Time).ToList();

        if (_isExpanded)
        {
            UpdateExpandedView();
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
        if(isPlaying) {_rotationClock.Controller?.Resume(); _lyricScrollClock?.Controller?.Resume();}
        else {_rotationClock.Controller?.Pause(); _lyricScrollClock?.Controller?.Pause();}
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
                if(!changed)
                {
                    _seekError="播放器未执行跳转";
                    _compactSeek.ToolTip=_expandedSeek.ToolTip=_seekError;
                    _pendingSeekFraction=null;
                    break;
                }
                OnUpdateTick(null,EventArgs.Empty);
                OnLyricTick(null,EventArgs.Empty);
            }
        }
        catch(Exception ex) {Program.Log("Island seek: "+ex.Message);}
        finally {_seeking=false; if(!_closed)OnUpdateTick(null,EventArgs.Empty);}
    }

    private void OnUpdateTick(object? sender, EventArgs e)
    {
        if(_closed)return;
        var timeline=_mediaSession.ReadTimeline();
        if(_currentMedia is null || timeline is null)return;
        _currentMedia.Position=timeline.Value.Position;
        _currentMedia.Duration=timeline.Value.Duration;
        _currentMedia.IsPlaying=timeline.Value.Playing;
        _positionUpdateTime=DateTime.Now;
        UpdatePlayPauseButton(_currentMedia.IsPlaying);
        UpdateRotation(_currentMedia.IsPlaying);
        double duration=_currentMedia.Duration.TotalSeconds;
        foreach(var seek in new[]{_compactSeek,_expandedSeek})
        {
            seek.IsEnabled=timeline.Value.CanSeek && duration>0;
            seek.ToolTip=_seekError??(duration>0?(timeline.Value.CanSeek?"播放进度":"播放器不支持跳转"):"播放器未提供实时进度");
            if(!seek.Dragging && !_seeking)seek.Progress=duration>0?_currentMedia.Position.TotalSeconds/duration:0;
            seek.InvalidateVisual();
        }
        int second=(int)_currentMedia.Position.TotalSeconds;
        int durationSecond=(int)_currentMedia.Duration.TotalSeconds;
        if(_displayedSecond!=second) {_displayedSecond=second; _currentTime.Text=FormatTime(_currentMedia.Position);}
        if(_displayedDuration!=durationSecond) {_displayedDuration=durationSecond; _totalTime.Text=FormatTime(_currentMedia.Duration);}
    }

    private void OnLyricTick(object? sender, EventArgs e)
    {
        if (_currentMedia == null || _currentLyrics == null || _currentLyrics.Count == 0)
        {
            SetLyricText(_currentMedia?.Title ?? "等待播放...");
            return;
        }

        var elapsed = _currentMedia.IsPlaying?DateTime.Now - _positionUpdateTime:TimeSpan.Zero;
        var currentPosition = _currentMedia.Position + elapsed;

        int low=0,high=_currentLyrics.Count-1,index=-1;
        while(low<=high)
        {
            int middle=low+(high-low)/2;
            if(_currentLyrics[middle].Time<=currentPosition) {index=middle; low=middle+1;}
            else high=middle-1;
        }
        if(index>=0)
        {
            var currentLine=_currentLyrics[index];
            var nextLine=index+1<_currentLyrics.Count?_currentLyrics[index+1]:null;
            SetLyricText(currentLine.Text,nextLine is null?6:Math.Max(1,(nextLine.Time-currentPosition).TotalSeconds));

            if (_isExpanded && _highlightedLine!=currentLine)
            {
                _highlightedLine=currentLine;
                HighlightCurrentLyric(currentLine);
            }
        }
    }

    private void SetLyricText(string text,double remainingSeconds=6)
    {
        if(_lyricText.Text==text)return;
        _lyricText.Text=text;
        _lyricScrollSeconds=remainingSeconds;
        StartLyricScroll();
    }

    private void StartLyricScroll()
    {
        _lyricScrollClock?.Controller?.Remove();
        _lyricScrollClock=null;
        _lyricScroll.BeginAnimation(TranslateTransform.XProperty,null);
        _lyricText.Width=double.NaN;
        _lyricText.Measure(new Size(double.PositiveInfinity,42));
        double natural=_lyricText.DesiredSize.Width;
        _lyricText.Width=natural;
        double overflow=Math.Max(0,natural-_lyricViewport.ActualWidth);
        _lyricScroll.X=overflow>0?0:Math.Max(0,(_lyricViewport.ActualWidth-natural)/2);
        if(overflow<=0 || _isExpanded || _lyricViewport.ActualWidth<1)return;
        var animation=new DoubleAnimation(0,-overflow,TimeSpan.FromSeconds(Math.Clamp(_lyricScrollSeconds-.6,.8,12))) {BeginTime=TimeSpan.FromMilliseconds(600),FillBehavior=FillBehavior.HoldEnd};
        _lyricScrollClock=(AnimationClock)animation.CreateClock(true);
        _lyricScroll.ApplyAnimationClock(TranslateTransform.XProperty,_lyricScrollClock);
        if(_currentMedia?.IsPlaying!=true)_lyricScrollClock.Controller?.Pause();
    }

    private void HighlightCurrentLyric(LyricLine currentLine)
    {
        foreach (var child in _lyricsPanel.Children.OfType<TextBlock>())
        {
            if (child.Tag as LyricLine == currentLine)
            {
                child.FontWeight = FontWeights.SemiBold;
                child.Opacity = 1.0;
                child.Foreground = new SolidColorBrush(Color.FromRgb(209, 250, 107));

                // 滚动到当前行
                child.BringIntoView();
            }
            else
            {
                child.FontWeight = FontWeights.Normal;
                child.Opacity = 0.45;
                child.Foreground = Brushes.White;
            }
        }
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
        _transitioning=true;
        double center=Left+ActualWidth/2;
        var duration=TimeSpan.FromMilliseconds(320);
        var easing=new CubicEase {EasingMode=EasingMode.EaseOut};
        double width=_isExpanded?532:492;
        double height=_isExpanded?272:56;
        double left=center-width/2;
        _expandedContent.Visibility=Visibility.Visible;
        _expandedContent.IsHitTestVisible=_isExpanded;
        if(_isExpanded)UpdateExpandedView();
        var heightAnimation=new DoubleAnimation(ActualHeight,height,duration) {EasingFunction=easing};
        heightAnimation.Completed+=(_,_)=> {
            if(_closed || revision!=_transitionRevision)return;
            SetCurrentValue(WidthProperty,width);
            SetCurrentValue(HeightProperty,height);
            SetCurrentValue(LeftProperty,left);
            BeginAnimation(WidthProperty,null);
            BeginAnimation(HeightProperty,null);
            BeginAnimation(LeftProperty,null);
            _lyricText.SetCurrentValue(OpacityProperty,_isExpanded?0d:1d);
            _lyricText.BeginAnimation(OpacityProperty,null);
            _expandedContent.SetCurrentValue(OpacityProperty,_isExpanded?1d:0d);
            _expandedContent.BeginAnimation(OpacityProperty,null);
            _compactSeek.SetCurrentValue(MarginProperty,_isExpanded?new Thickness(146,0,20,16):new Thickness(16,0,16,0));
            _compactSeek.BeginAnimation(MarginProperty,null);
            _transitioning=false;
            if(!_isExpanded) {_expandedContent.Visibility=Visibility.Collapsed; StartLyricScroll();}
        };
        BeginAnimation(WidthProperty,new DoubleAnimation(ActualWidth,width,duration) {EasingFunction=easing});
        BeginAnimation(HeightProperty,heightAnimation);
        BeginAnimation(LeftProperty,new DoubleAnimation(Left,left,duration) {EasingFunction=easing});
        _lyricText.BeginAnimation(OpacityProperty,new DoubleAnimation(_isExpanded?0:1,duration) {EasingFunction=easing});
        var detailsFade=new DoubleAnimation(_isExpanded?1:0,duration) {EasingFunction=easing};
        _expandedContent.BeginAnimation(OpacityProperty,detailsFade);
        _compactSeek.BeginAnimation(MarginProperty,new ThicknessAnimation(_isExpanded?new Thickness(146,0,20,16):new Thickness(16,0,16,0),duration) {EasingFunction=easing});
    }

    private void OnIslandSizeChanged(object sender,SizeChangedEventArgs e)
    {
        if(ActualWidth<=12 || ActualHeight<=12)return;
        double progress=Math.Clamp((ActualHeight-56)/216,0,1);
        double radius=22+4*progress;
        SetSurface(new Rect(6,6,ActualWidth-12,ActualHeight-12),radius);
        _contentClip.Rect=new Rect(2,2,ActualWidth-16,ActualHeight-16);
        _contentClip.RadiusX=_contentClip.RadiusY=radius-2;

        // Follow the laid-out shell so render-thread transforms cannot overtake its resize.
        _coverScale.ScaleX=_coverScale.ScaleY=1+2.8*progress;
        _coverPlacement.X=-11*progress;
        _coverPlacement.Y=47*progress;
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
