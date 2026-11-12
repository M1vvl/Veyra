using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Services;
using GlassDock.Shell;

namespace GlassDock;

internal enum ControlSection { Overview,Wifi,Audio,Power,Calendar,Apps }

internal sealed class SidebarWindow : GlassWindow
{
    private const double Scale=.8;
    private const double RailWidth=74,RootWidth=146,HiddenOffset=146;
    private readonly StackPanel _stack=new() { Margin=new Thickness(8,10,8,10),HorizontalAlignment=HorizontalAlignment.Center };
    private readonly Sidebar.SidebarMagnetism _magnetic;
    private readonly TranslateTransform _contentShift=new();
    private readonly Sidebar.SidebarPullGesture _pull=new();
    private double _baseSurfaceHeight,_baseSurfaceTop;
    private readonly TranslateTransform _slide=new(HiddenOffset,0);
    private readonly DispatcherTimer _hoverTimer=new() { Interval=TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _clockTimer=new() { Interval=TimeSpan.FromSeconds(20) };
    private readonly DispatcherTimer _inputTimer=new() { Interval=TimeSpan.FromMilliseconds(450) };
    private readonly TextBlock _clock=Ui.Text("",12),_input=Ui.Text("—",19),_power=Ui.Text("",10,Ui.Muted);
    private readonly Sidebar.VolumeLevelIcon _volumeIcon=new() { Width=28,Height=28 };
    private readonly AudioService _audioState=new();
    private bool _readingVolume,_pressingControl;
    private long _lastVolumeRead;
    private readonly Border _handle=new() { Width=5,CornerRadius=new CornerRadius(2),Background=new SolidColorBrush(Color.FromArgb(100,125,154,190)),Cursor=Cursors.Hand,ToolTip="左拉切换固定 / 自动隐藏" };
    private readonly Border _handleArea=new() { Width=18,Background=Brushes.Transparent,Cursor=Cursors.SizeWE };
    private double _target=HiddenOffset,_velocity,_lastTime;
    private long _lastInside,_revealBlockedUntil;
    private bool _animating,_open;
    private bool _inputReading,_inputSwitching;
    private int _inputRevision;
    private double _contentHeight,_contentFit=1;
    private QuickControlsWindow? _panel;
    private bool _desktopSuspended;
    internal bool DesktopSuspended=>_desktopSuspended;
    internal bool CapturedInteraction=>_pressingControl || _pull.Tracking;
    internal void SetDesktopSuspended(bool suspended)
    {
        if(_desktopSuspended==suspended)return;
        _desktopSuspended=suspended;
        if(suspended)
        {
            _panel?.Close(); CancelPull(); _pressingControl=false; _open=false;
            _hoverTimer.Stop(); _clockTimer.Stop(); _inputTimer.Stop();
            CompositionTarget.Rendering-=Animate; _animating=false; _velocity=0; _lastTime=0;
            _magnetic.Leave(); _slide.X=_target=HiddenOffset;
            SurfaceVisible=false; Hide(); ReleaseMaterial(true);
        }
        else
        {
            Show(); _clockTimer.Start(); _=UpdateStatus(); ApplyPreferences();
        }
    }
    internal bool IsRailInteraction=>_pressingControl || ContentLayer.IsMouseOver || _pull.Tracking;
    public SidebarWindow() : base("Veyra · 系统侧栏")
    {
        _magnetic=new Sidebar.SidebarMagnetism(_stack);
        var context=new ContextMenu();
        context.Items.Add(Ui.Menu("新建映射盒…",()=>Program.DesktopBoxes?.PromptCreateBox()));
        context.Items.Add(Ui.Menu("管理收纳盒…",()=>Program.DesktopBoxes?.ShowSettings()));
        context.Items.Add(new Separator()); context.Items.Add(Ui.Menu("设置…",Program.OpenSettings));
        Root.ContextMenu=context;
        if(Program.TrayMenus is { } menus) menus.Changed+=TrayMenuChanged;
        _stack.RenderTransform=_contentShift;
        SurfaceVisible=false; ContentLayer.Background=Brushes.Transparent; ContentLayer.Children.Add(_stack);
        Fallback.RenderTransform=_slide; Glass.RenderTransform=_slide; ContentLayer.RenderTransform=_slide;
        var arrow=Ui.Glyph("\uE70E"); arrow.RenderTransformOrigin=new Point(.5,.5); arrow.RenderTransform=new RotateTransform(-90);
        _stack.Children.Add(Ui.Button(arrow,"向左展开隐藏托盘",()=>TogglePanel(ControlSection.Apps),42,42));
        Add("\uE7F4","控制中心",()=>TogglePanel(ControlSection.Overview));
        _input.HorizontalAlignment=HorizontalAlignment.Center; _input.VerticalAlignment=VerticalAlignment.Center;
        var inputArea=new Border { Width=42,Height=42,Background=Brushes.Transparent,Child=_input,Cursor=Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(inputArea,"中英文切换");
        inputArea.MouseLeftButtonUp+=async(_,e)=> { e.Handled=true; await ToggleInput(); };
        _stack.Children.Add(inputArea);
        Add("\uE701","Wi-Fi 与网络",()=>TogglePanel(ControlSection.Wifi));
        _stack.Children.Add(Ui.Button(_volumeIcon,"音量",()=>TogglePanel(ControlSection.Audio),42,42));
        var battery=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center }; battery.Children.Add(Ui.Glyph("\uE83F",23)); _power.HorizontalAlignment=HorizontalAlignment.Center; battery.Children.Add(_power);
        _stack.Children.Add(Ui.Button(battery,"电池与电源计划",()=>TogglePanel(ControlSection.Power),42,48));
        Divider(); _clock.TextAlignment=TextAlignment.Center; _clock.LineHeight=19;
        _stack.Children.Add(Ui.Button(_clock,"日期与日历",()=>TogglePanel(ControlSection.Calendar),48,53));
        Add("\uE713","Veyra 设置",Program.OpenSettings); Add("\uE740","显示桌面",ShellService.Desktop);
        _handle.HorizontalAlignment=HorizontalAlignment.Right; _handle.Cursor=Cursors.SizeWE;
        _handleArea.Child=_handle; Root.Children.Add(_handleArea);
        _handleArea.MouseEnter+=(_,_)=>Reveal();
        _handleArea.MouseMove+=(_,_)=> { if(!_open && !_pull.Tracking) Reveal(); };
        _handleArea.PreviewMouseLeftButtonDown+=BeginPull;
        _handleArea.PreviewMouseMove+=MovePull;
        _handleArea.PreviewMouseLeftButtonUp+=EndPull;
        _handleArea.LostMouseCapture+=(_,e)=> { if(ReferenceEquals(e.OriginalSource,_handleArea) && _pull.Tracking && Mouse.Captured!=_handleArea) CancelPull(); };
        Root.PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape && _pull.Tracking) { CancelPull(); e.Handled=true; } };
        ContentLayer.MouseEnter+=(_,_)=>Reveal(); _hoverTimer.Tick+=(_,_)=>CheckAutoHide(); _clockTimer.Tick+=async(_,_)=>await UpdateStatus();
        ContentLayer.MouseMove+=(_,e)=> {
            if(Program.CapturePreview || _pull.Tracking || _pressingControl) return;
            if(_magnetic.PointAt(e.GetPosition(_stack).Y)) StartAnimation();
        };
        ContentLayer.MouseLeave+=(_,_)=> { if(!Program.CapturePreview) { _magnetic.Leave(); StartAnimation(); } };
        ContentLayer.PreviewMouseLeftButtonDown+=(_,_)=>_pressingControl=true;
        ContentLayer.PreviewMouseLeftButtonUp+=(_,_)=> { _pressingControl=false; StartAnimation(); };
        ContentLayer.LostMouseCapture+=(_,_)=> { if(Mouse.LeftButton!=MouseButtonState.Pressed)_pressingControl=false; };
        _inputTimer.Tick+=async(_,_)=>await RefreshInput();
        Loaded+=async(_,_)=> { Reposition(); await UpdateStatus(); if(!_desktopSuspended)_clockTimer.Start(); ApplyPreferences(); };
        Closed+=(_,_)=> { if(Program.TrayMenus is { } menus) menus.Changed-=TrayMenuChanged; CancelPull(); _audioState.Dispose(); _hoverTimer.Stop(); _clockTimer.Stop(); _inputTimer.Stop(); CompositionTarget.Rendering-=Animate; _panel?.Close(); };
    }
    private Button Add(string glyph,string label,Action action) { var button=Ui.Button(Ui.Glyph(glyph),label,action,42,42); _stack.Children.Add(button); return button; }
    private void Divider()=>_stack.Children.Add(new Border { Height=1,Width=26,Background=new SolidColorBrush(Color.FromArgb(28,65,80,100)),Margin=new Thickness(0,7,0,7) });
    protected override void Reposition()
    {
        if(!IsLoaded) return;
        var m=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
        // Fit the actual controls, including equal top/bottom padding, rather than
        // reserving a fixed-height surface below the last button.
        _stack.LayoutTransform=Transform.Identity;
        _stack.Measure(new Size(74,double.PositiveInfinity));
        _contentHeight=Math.Ceiling(_stack.DesiredSize.Height);
        const double reserve=120;
        double availableHeight=Math.Max(1,((m.Bottom-m.Top)/dpi.DpiScaleY-24)/Scale-20-reserve);
        _contentFit=Math.Min(1,availableHeight/Math.Max(1,_contentHeight));
        double logicalHeight=_contentHeight*_contentFit+20+reserve;
        ContentScale=Scale; Root.Width=RootWidth; Root.Height=logicalHeight; Root.RenderTransform=new ScaleTransform(Scale,Scale);
        Root.HorizontalAlignment=HorizontalAlignment.Left; Root.VerticalAlignment=VerticalAlignment.Top; Width=RootWidth*Scale; Height=logicalHeight*Scale;
        Left=m.Right/dpi.DpiScaleX-Width; Top=m.Top/dpi.DpiScaleY+((m.Bottom-m.Top)/dpi.DpiScaleY-Height)/2;
        _baseSurfaceTop=10+reserve/2;
        _baseSurfaceHeight=_contentHeight*_contentFit;
        SetSurface(new Rect(RootWidth-9-RailWidth,_baseSurfaceTop,RailWidth,_baseSurfaceHeight),25);
        _contentShift.Y=0;
        _stack.LayoutTransform=_contentFit<1?new ScaleTransform(_contentFit,_contentFit):Transform.Identity;
        _stack.UpdateLayout(); _magnetic.RefreshLayout();
        Canvas.SetLeft(_handleArea,RootWidth-_handleArea.Width); Canvas.SetTop(_handleArea,_baseSurfaceTop+_baseSurfaceHeight*.22); _handleArea.Height=_baseSurfaceHeight*.56; _panel?.PositionBeside(this);
        var trayStart=PointToScreen(new Point()); var trayEnd=PointToScreen(new Point(Width,Height));
        Program.TrayHost?.UpdateHostBounds(new Rect(trayStart,trayEnd));
    }
    internal Rect ExpandedSurfaceScreenBounds=>new(Root.PointToScreen(Surface.TopLeft),Root.PointToScreen(Surface.BottomRight));
    internal object InspectLayout()
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        var contentTop=_stack.PointToScreen(new Point());
        var contentBottom=_stack.PointToScreen(new Point(_stack.ActualWidth,_stack.ActualHeight));
        var surface=ExpandedSurfaceScreenBounds;
        return new {
            DpiScaleX=dpi.DpiScaleX,DpiScaleY=dpi.DpiScaleY,Scale,
            WindowDip=Bounds(new Rect(Left,Top,Width,Height)),
            WindowPhysical=Bounds(new Rect(PointToScreen(new Point()),PointToScreen(new Point(Width,Height)))),
            SurfacePhysical=Bounds(surface),ContentPhysical=Bounds(new Rect(contentTop,contentBottom)),
            MaterialPhysical=Bounds(new Rect(Glass.PointToScreen(new Point()),Glass.PointToScreen(new Point(Glass.Width,Glass.Height)))),
            ContentHeight=_contentHeight,ContentFit=_contentFit,
            Magnetism=_magnetic.Inspect(),
            ReducedMotion=Program.Prefs.ReducedMotion,SystemAnimationEnabled=SystemParameters.ClientAreaAnimation,
            Pinned=Program.Prefs.SidebarPinned,PullTracking=_pull.Tracking,PullReady=_pull.Ready,
            VolumeBands=_volumeIcon.ActiveBands,
            HandlePhysical=Bounds(new Rect(_handleArea.PointToScreen(new Point()),_handleArea.PointToScreen(new Point(_handleArea.ActualWidth,_handleArea.ActualHeight)))),
            ContentTopPaddingPhysical=contentTop.Y-surface.Top,
            ContentBottomPaddingPhysical=surface.Bottom-contentBottom.Y,
            Panel=_panel?.InspectLayout()
        };
    }
    private static object Bounds(Rect rect)=>new { X=rect.X,Y=rect.Y,Width=rect.Width,Height=rect.Height };
    internal void UpdateTrayHostBounds()=>Reposition();
    public override void ApplyPreferences()
    {
        base.ApplyPreferences();
        _panel?.ApplyPreferences();
        UpdatePullFeedback();
        if(Program.Prefs.SidebarPinned) Reveal(); else if(_panel?.IsVisible!=true) SetOpen(false);
    }
    private void Reveal()
    {
        if(_desktopSuspended)return;
        if(!Program.Prefs.SidebarPinned && Environment.TickCount64<_revealBlockedUntil) return;
        _lastInside=Environment.TickCount64; SetOpen(true); _hoverTimer.Start();
    }
    internal void RevealForCapture()=>Reveal();
    internal void PreviewMagnetism(bool active)
    {
        if(active) _magnetic.PointAt(_stack.ActualHeight*.45); else _magnetic.Leave();
        StartAnimation();
    }
    internal void PreviewPointer(double? fraction)
    {
        if(fraction is double value) _magnetic.PointAt(_stack.ActualHeight*Math.Clamp(value,0,1)); else _magnetic.Leave();
        StartAnimation();
    }
    private void TogglePanel(ControlSection section)
    {
        if(_panel is { IsVisible:true } && _panel.CurrentSection==section) { _panel.Close(); return; }
        OpenPanel(section);
    }
    internal void OpenPanel(ControlSection section)
    {
        var target=DesktopNative.GetForegroundWindow(); _revealBlockedUntil=0; Reveal();
        if(_panel is null) { _panel=new QuickControlsWindow(this,target); _panel.Closed+=(_,_)=> { _panel=null; _lastInside=Environment.TickCount64; _hoverTimer.Start(); }; }
        if(!_panel.IsVisible)_panel.Show();
        _panel.PositionBeside(this); _panel.Activate(); _panel.ShowSection(section);
    }
    private void CheckAutoHide()
    {
        if(_desktopSuspended) { _hoverTimer.Stop(); return; }
        if(_open && Environment.TickCount64-_lastVolumeRead>=350)_=RefreshVolume();
        if(Program.CapturePreview) return;
        bool inside=false;
        if(GetCursorPos(out var p))
        {
            var point=new Point(p.X,p.Y); var logical=Root.PointFromScreen(point);
            inside=_open && new Rect(Surface.Left+_slide.X,Surface.Top,Surface.Width,Surface.Height).Contains(logical);
            if(!Program.CapturePreview && !_pull.Tracking && !_pressingControl)
            {
                bool changed=inside?_magnetic.PointAt(_stack.PointFromScreen(point).Y):_magnetic.Leave();
                if(changed) StartAnimation();
            }
        }
        if(_panel is { IsVisible:true,IsActive:false,IsMouseOver:false } && !inside && !_pressingControl && Program.TrayMenus?.Active!=true)_panel.Close();
        if(Program.Prefs.SidebarPinned || _panel?.IsVisible==true || Program.TrayMenus?.Active==true || _pull.Tracking || inside || _handleArea.IsMouseOver)
        { _lastInside=Environment.TickCount64; return; }
        if(Environment.TickCount64-_lastInside>=450) { SetOpen(false); _hoverTimer.Stop(); }
    }
    private void SetOpen(bool open)
    {
        if(_desktopSuspended)return;
        if(open) { _inputTimer.Start(); _=RefreshInput(); } else _inputTimer.Stop();
        _open=open; _target=open?0:HiddenOffset; if(open) SurfaceVisible=true;
        if(!open) _magnetic.Leave();
        if(Program.Prefs.ReducedMotion || !SystemParameters.ClientAreaAnimation)
        {
            _slide.X=_target;
            if(Program.Prefs.ReducedMotion || !open) UpdateMagnetism(0,true);
            if(!open) { SurfaceVisible=false; ReleaseMaterial(); }
            else { RenderGlass(); if(!Program.Prefs.ReducedMotion) StartAnimation(); }
            return;
        }
        StartAnimation();
    }
    private void StartAnimation()
    {
        if(!_animating) { _animating=true; _lastTime=0; CompositionTarget.Rendering+=Animate; }
    }
    private void Animate(object? sender,EventArgs e)
    {
        if(e is not RenderingEventArgs r) return;
        double now=r.RenderingTime.TotalSeconds,dt=_lastTime==0?1d/60:Math.Clamp(now-_lastTime,0,.033); _lastTime=now;
        const double omega=2*Math.PI/.30; double d=_slide.X-_target,c=_velocity+omega*d,t=Math.Exp(-omega*dt);
        _slide.X=_target+(d+c*dt)*t; _velocity=(_velocity-omega*c*dt)*t;
        bool magneticMoving=UpdateMagnetism(dt,Program.Prefs.ReducedMotion);
        if(Math.Abs(_slide.X-_target)<.05 && Math.Abs(_velocity)<.1 && !magneticMoving) { _slide.X=_target; _velocity=0; _animating=false; CompositionTarget.Rendering-=Animate; if(!_open) { SurfaceVisible=false; ReleaseMaterial(); } }
    }
    private bool UpdateMagnetism(double elapsed,bool reduced)
    {
        bool moving=!_pressingControl && _magnetic.Update(elapsed,reduced,Program.Prefs.Magnification);
        double expansion=_magnetic.Expansion*_contentFit;
        double top=_baseSurfaceTop-expansion/2,height=_baseSurfaceHeight+expansion;
        double width=Math.Max(RailWidth,_magnetic.RequiredWidth*_contentFit),left=RootWidth-9-width;
        if(Math.Abs(Surface.Top-top)>.03 || Math.Abs(Surface.Height-height)>.03 || Math.Abs(Surface.Width-width)>.03)
        {
            SetSurface(new Rect(left,top,width,height),25);
            _contentShift.Y=expansion/2;
            _panel?.PositionBeside(this);
        }
        return moving;
    }

    private Point GesturePoint(MouseEventArgs e)
    {
        var screen=Root.PointToScreen(e.GetPosition(Root)); var dpi=VisualTreeHelper.GetDpi(this);
        return new Point(screen.X/dpi.DpiScaleX,screen.Y/dpi.DpiScaleY);
    }
    private void BeginPull(object sender,MouseButtonEventArgs e)
    {
        _revealBlockedUntil=0; Reveal();
        _pull.Begin(GesturePoint(e)); _lastInside=Environment.TickCount64;
        if(!_handleArea.CaptureMouse()) CancelPull();
        e.Handled=true;
    }
    private void MovePull(object sender,MouseEventArgs e)
    {
        if(!_pull.Tracking || e.LeftButton!=MouseButtonState.Pressed) return;
        _pull.Update(GesturePoint(e));
        if(!_pull.Dragging) return;
        if(Mouse.Captured!=_handleArea) { CancelPull(); return; }
        _magnetic.Leave(); StartAnimation(); UpdatePullFeedback(); e.Handled=true;
    }
    private void EndPull(object sender,MouseButtonEventArgs e)
    {
        if(!_pull.Tracking) return;
        _pull.Update(GesturePoint(e)); var result=_pull.End();
        if(Mouse.Captured==_handleArea) _handleArea.ReleaseMouseCapture();
        if(result.Toggle)
        {
            Program.Prefs.SidebarPinned=!Program.Prefs.SidebarPinned; Program.Prefs.Save();
            if(Program.Prefs.SidebarPinned) Reveal();
            else
            {
                _revealBlockedUntil=Environment.TickCount64+250;
                _panel?.Close(); _hoverTimer.Stop(); SetOpen(false);
            }
        }
        _lastInside=Environment.TickCount64; UpdatePullFeedback();
        e.Handled=true;
    }
    private void CancelPull()
    {
        _pull.End(true); if(Mouse.Captured==_handleArea) _handleArea.ReleaseMouseCapture(); UpdatePullFeedback();
    }
    private void UpdatePullFeedback()
    {
        _handle.Opacity=_pull.Ready?1:Program.Prefs.SidebarPinned?.85:.45;
        _handle.Width=_pull.Ready?6:5;
        string tip=_pull.Ready?(Program.Prefs.SidebarPinned?"松开收起":"松开固定")
            :Program.Prefs.SidebarPinned?"左拉竖条：收起":"左拉竖条：固定";
        _handle.ToolTip=_handleArea.ToolTip=tip;
    }
    private void TrayMenuChanged(object? sender,EventArgs e)
    {
        if(Program.TrayMenus?.Active==true) { _revealBlockedUntil=0; Reveal(); return; }
        if(_panel is { IsActive:false,IsMouseOver:false }) _panel.Close();
        _lastInside=Environment.TickCount64; _hoverTimer.Start();
    }
    private async Task UpdateStatus()
    {
        _clock.Text=DateTime.Now.ToString("HH:mm\nMM/dd"); var battery=PowerService.ReadBattery(); _power.Text=battery.Present?(battery.Percent is int percent?percent+"%":"—"):"电源";
        await RefreshInput();
        await RefreshVolume();
    }
    private async Task RefreshVolume()
    {
        if(_readingVolume)return; _readingVolume=true; _lastVolumeRead=Environment.TickCount64;
        try { var state=await Task.Run(_audioState.Read); if(IsLoaded)_volumeIcon.Update(state); }
        finally { _readingVolume=false; }
    }
    internal void ObserveAudioState(AudioState state)=>_volumeIcon.Update(state);
    private async Task RefreshInput()
    {
        if(_inputReading || _inputSwitching) return;
        int revision=_inputRevision;
        _inputReading=true;
        try { var state=await Task.Run(()=>InputMethodService.Read()); if(!_inputSwitching && revision==_inputRevision) ApplyInputState(state); }
        finally { _inputReading=false; }
    }
    private void ApplyInputState(Services.InputMethodState state)
    {
        _input.Text=state.Indicator;
        _input.ToolTip=state.CanToggleComposition?"点击切换中 / 英":state.Error??"中 / 英";
    }
    private async Task ToggleInput()
    {
        if(_inputSwitching) return;
        _inputSwitching=true; _inputRevision++;
        try
        {
            var result=await Task.Run(()=> {
                var before=InputMethodService.Read();
                var operation=InputMethodService.ToggleComposition(before.TargetWindow);
                return (operation,state:InputMethodService.Read());
            });
            ApplyInputState(result.state);
            if(!result.operation.Verified) _input.ToolTip=result.operation.Error??"切换未完成";
        }
        finally { _inputSwitching=false; }
    }
    protected override void OnMaterialLuminance(double luminance)
    { var ink=luminance<.48?Brushes.White:Ui.Ink; SetTextColor(_stack,ink); _volumeIcon.Ink=ink; }
    private static void SetTextColor(DependencyObject node,Brush color) { if(node is TextBlock text) text.Foreground=color; for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) SetTextColor(VisualTreeHelper.GetChild(node,i),color); }
    [StructLayout(LayoutKind.Sequential)] private struct ScreenPoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);
}
