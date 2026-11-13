using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GlassDock.Models;
using GlassDock.Services;
using GlassDock.Shell;
using GlassDock.Docking;

namespace GlassDock;

internal sealed class DockWindow : GlassWindow
{
    private readonly Canvas _icons=new() { ClipToBounds=false };
    private readonly Border _runningDivider=new() {
        Width=2.5,Height=38,CornerRadius=new CornerRadius(1.25),IsHitTestVisible=false,Visibility=Visibility.Collapsed,
        Background=new LinearGradientBrush(Color.FromArgb(220,255,255,255),Color.FromArgb(115,116,135,157),new Point(0,0),new Point(1,0)),
        Effect=new DropShadowEffect { BlurRadius=4,ShadowDepth=1.5,Opacity=.45,Color=Colors.Black }
    };
    private int _runningBoundary=-1;
    private readonly List<DockItem> _items=new();
    private readonly Dictionary<string,DockItem> _itemCache=new(StringComparer.Ordinal);
    private readonly Border _approach=new() { Background=Brushes.Transparent };
    private readonly DockSpring _materialWidth=new(),_insertionGap=new();
    private readonly DockDragState _drag=new();
    private readonly ExternalDragVisual _externalVisual=new();
    private IReadOnlyList<PinnedApp>? _previewPins;
    private IReadOnlyList<PinnedApp>? _dragPins;
    private double _baseWidthTarget;
    private Point? _entryPoint;
    private Button? _moreButton;
    private readonly List<PinnedApp> _pins;
    private readonly WindowDiscovery _discovery;
    private bool _refreshingWindows,_closed;
    private IReadOnlyList<PinnedApp> _runningApps=Array.Empty<PinnedApp>();
    private IReadOnlyList<WindowSnapshot> _windows=Array.Empty<WindowSnapshot>();
    private readonly DispatcherTimer _statusTimer=new() { Interval=TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _activeTimer=new() { Interval=TimeSpan.FromMilliseconds(220) };
    private readonly DesktopNative.WinEventCallback _foregroundCallback;
    private nint _foregroundHook;
    private DockLayerService? _layer;
    private bool _started;
    private bool _desktopSuspended;
    private bool _previewResourceSuspended;
    internal bool DesktopSuspended=>_desktopSuspended;
    internal void PreviewResourceSuspension(bool suspended) { _previewResourceSuspended=suspended; UpdateDesktopPresentation(); }
    internal bool DesktopVisible => WindowState!=WindowState.Minimized && DesktopNative.IsWindowVisible(new WindowInteropHelper(this).Handle);
    internal Rect VisibleSurfaceScreenBounds=>new(Root.PointToScreen(Surface.TopLeft),Root.PointToScreen(Surface.BottomRight));
    private nint _lastForeground;
    private readonly PinStore _store=new();
    private double _pointerX=double.NaN, _lastTime;
    private bool _animating;
    private int _page;
    private int _capacity=12;
    private string _runningKey="";
    private int _allCount;
    internal DockWindow(WindowDiscovery? discovery=null) : base("Veyra · Dock")
    {
        _discovery=discovery??new WindowDiscovery();
        Topmost=false;
        Opacity=0;
        _foregroundCallback=OnForegroundChanged;
        _pins=_store.Load().ToList();
        if(_pins.Count==0 && _store.LastLoadError is null && !File.Exists(System.IO.Path.Combine(Preferences.DataDirectory,"first-run")))
        {
            SeedPins(); Directory.CreateDirectory(Preferences.DataDirectory); File.WriteAllText(System.IO.Path.Combine(Preferences.DataDirectory,"first-run"),"1");
        }
        Root.Children.Insert(0,_approach); Root.AllowDrop=true;
        ContentLayer.Children.Add(_icons);
        _icons.Children.Add(_runningDivider);
        ContentLayer.Background=Brushes.Transparent;
        ContentLayer.MouseMove+=(_,e)=> { if(!Program.CapturePreview && !_drag.Over) { _pointerX=e.GetPosition(Root).X; StartAnimation(); } };
        ContentLayer.MouseLeave+=(_,_)=> { if(!Program.CapturePreview) { _pointerX=double.NaN; StartAnimation(); } };
        Root.DragEnter+=DragOverDock; Root.DragOver+=DragOverDock; Root.Drop+=DropDock; Root.DragLeave+=LeaveDock;
        ContentLayer.MouseWheel+=(_,e)=> { if(_allCount>_capacity-2) { _page=Math.Clamp(_page+(e.Delta<0?1:-1),0,Math.Max(0,_allCount-_capacity+2)); Rebuild(); } e.Handled=true; };
        var menu=new ContextMenu(); menu.Items.Add(Ui.Menu("添加应用…",AddWithDialog));
        menu.Items.Add(Ui.Menu("新建映射盒…",()=>Program.DesktopBoxes?.PromptCreateBox()));
        menu.Items.Add(Ui.Menu("管理收纳盒…",()=>Program.DesktopBoxes?.ShowSettings()));
        menu.Items.Add(Ui.Menu("Dock 设置…",Program.OpenSettings)); menu.Items.Add(new Separator()); menu.Items.Add(Ui.Menu("恢复 Windows 任务栏",()=> { Program.Prefs.ReplaceTaskbar=false; Program.ApplyPreferences(); })); menu.Items.Add(Ui.Menu("退出 Veyra",Program.Quit)); ContentLayer.ContextMenu=menu;
        Loaded+=(_,_)=> {
            if(_started) return;
            _started=true;
            _layer=new DockLayerService(this);
            _layer.PresentationChanged+=(_,_)=>UpdateDesktopPresentation();
            Reposition(); RefreshWindows(); UpdateDesktopPresentation();
            _foregroundHook=DesktopNative.WatchForeground(_foregroundCallback);
            if(_foregroundHook==0) Program.Log("Foreground notification unavailable; desktop placement uses the fallback timer.");
            _statusTimer.Start(); _activeTimer.Start();
            var h=new WindowInteropHelper(this).Handle;
            if(!DesktopNative.RegisterHotKey(h,1,0x0002|0x0004|0x4000,0x47)) Program.Log("Ctrl+Alt+G hotkey unavailable");
        };
        _activeTimer.Tick+=(_,_)=> { UpdateDesktopPresentation(); UpdateActiveFrame(); };
        _statusTimer.Tick+=(_,_)=>RefreshWindows();
        Closed+=(_,_)=> {
            _closed=true;
            if(_foregroundHook!=0) DesktopNative.UnhookWinEvent(_foregroundHook);
            _layer?.Dispose();
            _statusTimer.Stop(); _activeTimer.Stop(); StopAnimation();
            _externalVisual.Dispose();
            DesktopNative.UnregisterHotKey(new WindowInteropHelper(this).Handle,1);
        };
    }
    private void OnForegroundChanged(nint hook,uint eventType,nint hwnd,int objectId,int childId,uint eventThread,uint eventTime)
    {
        if(!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(UpdateDesktopPresentation));
    }
    private void UpdateDesktopPresentation()
    {
        if(double.IsFinite(_pointerX) && !_drag.Over && !Program.CapturePreview)
        {
            if(GetCursorPos(out var cursor))
            {
                var pointer=Root.PointFromScreen(new Point(cursor.X,cursor.Y));
                if(pointer.X<Surface.Left || pointer.X>Surface.Right || pointer.Y<0 || pointer.Y>Root.Height)
                { _pointerX=double.NaN; StartAnimation(); }
            }
        }
        bool overlay=_layer?.Update()??false;
        bool render=!_previewResourceSuspended && (Program.CaptureDockPreview || overlay);
        Program.DesktopResources?.Update();
        if(!render && _drag.Over)return;
        if(!render)
        {
            SetDesktopSuspended(true); return;
        }
        bool restored=_desktopSuspended || !DesktopVisible;
        SetDesktopSuspended(false);
        Visibility=Visibility.Visible; Opacity=1; IsHitTestVisible=true;
        DesktopNative.EnsureNativeShown(this);
        if(SurfaceVisible==render && !restored) return;
        SurfaceVisible=render;
        RenderGlass();
    }
    internal void SetDesktopSuspended(bool suspended)
    {
        if(_desktopSuspended==suspended)return;
        _desktopSuspended=suspended;
        if(suspended)
        {
            StopAnimation(); _pointerX=double.NaN; _drag.Leave(); _externalVisual.Leave();
            _insertionGap.Snap(0); _materialWidth.Snap(_baseWidthTarget); _statusTimer.Stop();
            SurfaceVisible=false; Hide(); ReleaseMaterial(true);
            _icons.Children.Clear(); _icons.Children.Add(_runningDivider); _items.Clear(); _itemCache.Clear(); _moreButton=null;
            _windows=Array.Empty<WindowSnapshot>(); _runningKey="";
        }
        else
        {
            Show(); SurfaceVisible=true; RefreshWindows(); _statusTimer.Start(); RenderGlass();
        }
    }
    protected override nint WndProc(nint h,int msg,nint w,nint l,ref bool handled)
    {
        if(msg==0x0312 && w==1) { Program.OpenSettings(); handled=true; }
        return base.WndProc(h,msg,w,l,ref handled);
    }
    private void SeedPins()
    {
        var candidates=new[]{System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe"),System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft","Edge","Application","msedge.exe")};
        foreach(var p in candidates) if(File.Exists(p)) { try { _pins.Add(PinStore.CreatePin(p)); } catch(Exception ex) { Program.Log(ex.Message); } }
        SavePins();
    }
    private void SavePins() => _store.Save(_pins);
    protected override void OnMaterialLuminance(double luminance)
    {
        Brush foreground=luminance<.48?Brushes.White:Ui.Ink;
        foreach(var item in _items)
        {
            if(item.App is null && item.GlyphText is { } glyph) glyph.Foreground=foreground;
        }
    }
    protected override void Reposition()
    {
        if(!IsLoaded) return;
        var screen=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
        var screenWidth=(screen.Right-screen.Left)/dpi.DpiScaleX;
        double previousLeft=Left,previousScale=ContentScale;
        ContentScale=Program.Prefs.DockScale;
        Root.LayoutTransform=Transform.Identity;
        Root.RenderTransform=new ScaleTransform(ContentScale,ContentScale);
        Root.HorizontalAlignment=HorizontalAlignment.Left; Root.VerticalAlignment=VerticalAlignment.Top;
        double reserve=DockLayout.AnimationReserve(Program.Prefs.IconSize,Program.Prefs.Magnification);
        _capacity=Math.Max(4,(int)((screenWidth/ContentScale-reserve-44)/(Program.Prefs.IconSize+14)));
        _baseWidthTarget=Math.Min(screenWidth/ContentScale-reserve-24,DockLayout.BaseWidth(_items.Count>0?_items.Count:Math.Min(_capacity,_pins.Count+2),Program.Prefs.IconSize));
        if(_materialWidth.Value<1) _materialWidth.Snap(_baseWidthTarget);
        // Reserve one insertion slot in the native window. The visible surface stays compact;
        // only its narrow approach border participates in drag hit testing above the glass.
        double logicalWidth=Math.Min(screenWidth/ContentScale-24,Math.Max(_baseWidthTarget,_materialWidth.Value)+reserve);
        Root.Width=logicalWidth; Root.Height=172;
        Height=172*ContentScale; Width=logicalWidth*ContentScale;
        Left=screen.Left/dpi.DpiScaleX+(screenWidth-Width)/2;
        Top=screen.Bottom/dpi.DpiScaleY-Height-DesktopNative.BottomTaskbarClearance(this)+13*ContentScale;
        if(double.IsFinite(previousLeft) && previousScale>0)
        {
            foreach(var item in _items.Where(item=>item.Positioned))
                item.CenterX.Value=(previousLeft+item.CenterX.Value*previousScale-Left)/ContentScale;
            if(_entryPoint is Point point) _entryPoint=new Point((previousLeft+point.X*previousScale-Left)/ContentScale,point.Y);
        }
        LayoutFrame(0,Program.Prefs.ReducedMotion); RenderGlass(); StartAnimation();
    }
    private double _appliedIconSize=-1,_appliedDockScale=-1,_appliedMagnification=-1;
    public override void ApplyPreferences()
    {
        base.ApplyPreferences();
        bool icons=_appliedIconSize!=Program.Prefs.IconSize;
        bool layout=_appliedDockScale!=Program.Prefs.DockScale || _appliedMagnification!=Program.Prefs.Magnification;
        _appliedIconSize=Program.Prefs.IconSize; _appliedDockScale=Program.Prefs.DockScale; _appliedMagnification=Program.Prefs.Magnification;
        if(icons)Rebuild(); else if(layout)Reposition(); else if(_animating || Program.Prefs.ReducedMotion)StartAnimation();
    }
    private async void RefreshWindows()
    {
        if(_desktopSuspended || _closed || _refreshingWindows)return;
        _refreshingWindows=true;
        try
        {
            var snapshot=await _discovery.ReadAsync(_pins.ToArray());
            if(_closed || _desktopSuspended)return;
            _windows=snapshot.Windows;
            _runningApps=snapshot.Running;
            var ordered=GetOrderedApps(out int inactiveCount);
            var key=inactiveCount+":"+string.Join('|',ordered.Select(app=>app.Identity));
            if(key!=_runningKey) { _runningKey=key; Rebuild(); }
            UpdateActiveFrame(true);
        }
        catch(Exception ex) { Program.Log("Window discovery: "+ex.Message); }
        finally { _refreshingWindows=false; }
    }
    private void UpdateActiveFrame(bool force=false)
    {
        var foreground=DesktopNative.GetForegroundWindow();
        if(!force && foreground==_lastForeground) return;
        _lastForeground=foreground;
        foreach(var item in _items.Where(i=>i.App is not null))
        {
            bool active=WindowService.GetWindows(item.App!,_windows).Any(w=>w.Handle==foreground);
            item.ActiveFrame.Visibility=active?Visibility.Visible:Visibility.Hidden;
        }
    }
    private void Rebuild()
    {
        if(IsLoaded)
        {
            var bounds=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
            double width=(bounds.Right-bounds.Left)/dpi.DpiScaleX;
            _capacity=Math.Max(4,(int)((width/Program.Prefs.DockScale-DockLayout.AnimationReserve(Program.Prefs.IconSize,Program.Prefs.Magnification)-44)/(Program.Prefs.IconSize+14)));
        }
        var previous=_items.ToArray(); _items.Clear();
        AddItem("system:start",null,Ui.WindowsStart(46),"开始",OpenStart);
        AddItem("system:search",null,Ui.Glyph("\uE721",29),"搜索",ShellService.Search);
        var available=Math.Max(2,_capacity-2);
        var all=GetOrderedApps(out int inactiveCount);
        _allCount=all.Count;
        _page=Math.Clamp(_page,0,Math.Max(0,all.Count-available));
        _runningBoundary=all.Count>inactiveCount && _page+available>inactiveCount ? 2+Math.Max(0,inactiveCount-_page) : -1;
        foreach(var app in all.Skip(_page).Take(available))
        {
            if(_itemCache.TryGetValue(app.Identity,out var existing)) { AddItem(app.Identity,app,existing.Visual,app.Name,()=>WindowService.ActivateOrLaunch(app,_windows)); continue; }
            var image=new Image { Width=Program.Prefs.IconSize, Height=Program.Prefs.IconSize, Stretch=Stretch.Uniform };
            LoadIcon(image,app);
            RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
            AddItem(app.Identity,app,image,app.Name,()=>WindowService.ActivateOrLaunch(app,_windows));
        }
        foreach(var removed in previous.Where(item=>!_items.Contains(item)))
        {
            _icons.Children.Remove(removed.Button);
            _itemCache.Remove(removed.Key);
        }
        if(_moreButton is not null) { _icons.Children.Remove(_moreButton); _moreButton=null; }
        if(all.Count>available)
        {
            var more=Ui.Button(Ui.Glyph("\uE70D",14),"滚轮浏览更多应用",()=> { _page=(_page+available)%Math.Max(1,all.Count); Rebuild(); },18,30);
            _moreButton=more; _icons.Children.Add(more); Canvas.SetRight(more,8); Canvas.SetTop(more,24);
        }
        Reposition();
        _entryPoint=null;
    }
    private List<PinnedApp> GetOrderedApps(out int inactiveCount)
    {
        var pins=(_previewPins??_pins).DistinctBy(app=>app.Identity).ToArray();
        var inactive=new List<PinnedApp>();
        var active=new List<PinnedApp>();
        var represented=new HashSet<nint>();
        foreach(var pin in pins)
        {
            var windows=WindowService.GetWindows(pin,_windows);
            if(windows.Count==0) inactive.Add(pin);
            else { active.Add(pin); foreach(var window in windows) represented.Add(window.Handle); }
        }
        foreach(var running in _runningApps)
        {
            var windows=WindowService.GetWindows(running,_windows);
            if(windows.Count==0 || windows.All(window=>represented.Contains(window.Handle))) continue;
            active.Add(running);
            foreach(var window in windows) represented.Add(window.Handle);
        }
        inactiveCount=inactive.Count;
        inactive.AddRange(active);
        return inactive;
    }
    private static async void LoadIcon(Image image,PinnedApp app)
    {
        try { image.Source=await IconService.GetIconAsync(app,96); } catch(Exception ex) { Program.Log(ex.Message); }
    }
    internal void OpenStart()
    {
        var button=_items.FirstOrDefault(item=>item.Key=="system:start")?.Button;
        if(button is null || PresentationSource.FromVisual(button) is null) return;
        StartMenuPlacement.Toggle(this,new Rect(button.PointToScreen(new Point()),
            button.PointToScreen(new Point(button.ActualWidth,button.ActualHeight))));
    }
    private void AddItem(string key,PinnedApp? app,FrameworkElement visual,string label,Action action)
    {
        double size=Program.Prefs.IconSize;
        if(_itemCache.TryGetValue(key,out var retained))
        {
            retained.App=app; retained.Button.Width=retained.Button.Height=size+10;
            retained.Content.Width=retained.Content.Height=size+8;
            if(retained.Visual is Image retainedImage) retainedImage.Width=retainedImage.Height=size;
            retained.Scaling.CenterX=(size+10)/2; retained.Scaling.CenterY=size+10;
            _items.Add(retained); return;
        }
        var content=new Grid { Width=size+8,Height=size+8 };
        var frost=new LinearGradientBrush(Color.FromArgb(42,255,255,255),Color.FromArgb(8,219,230,244),new Point(0,0),new Point(0,1));
        var edge=new LinearGradientBrush(Color.FromArgb(150,255,255,255),Color.FromArgb(36,178,195,215),new Point(0,0),new Point(0,1));
        var activeFrame=new Border { CornerRadius=new CornerRadius(13),Background=frost,BorderBrush=edge,BorderThickness=new Thickness(.8),Visibility=Visibility.Hidden,Effect=new DropShadowEffect { Color=Color.FromRgb(12,24,43),BlurRadius=8,ShadowDepth=2,Opacity=.16,Direction=270 } };
        content.Children.Add(activeFrame); content.Children.Add(visual);
        var button=Ui.Button(content,label,action,size+10,size+10,22); button.RenderTransformOrigin=new Point(.5,1);
        button.Focusable=false; // Pointer focus must never outlive the actual foreground window.
        ToolTipService.SetInitialShowDelay(button,400);
        var item=new DockItem(key,button,app,content,visual) { ActiveFrame=activeFrame,GlyphText=visual as TextBlock };
        if(_entryPoint is Point entry)
        {
            item.CenterX.Snap(entry.X); item.CenterY.Snap(entry.Y); item.Positioned=true;
            item.Scale.Snap(1); item.Opacity.Snap(1);
        }
        item.Scaling.CenterX=(size+10)/2; item.Scaling.CenterY=size+10;
        var transforms=new TransformGroup(); transforms.Children.Add(item.Scaling); transforms.Children.Add(item.Position);
        button.RenderTransformOrigin=new Point(0,0); button.RenderTransform=transforms;
        _itemCache[key]=item; _items.Add(item); _icons.Children.Add(button);
        if(app is null) return;
        Point pressed=default;
        button.PreviewMouseLeftButtonDown+=(_,e)=>pressed=e.GetPosition(Root);
        button.PreviewMouseMove+=(_,e)=> {
            var current=item.App;
            if(e.LeftButton!=MouseButtonState.Pressed || current is null || !_pins.Any(p=>p.Id==current.Id) || _drag.PinId is not null) return;
            if((e.GetPosition(Root)-pressed).Length<8) return;
            _drag.PinId=current.Id; _drag.External=false; _drag.Over=true;
            _drag.PointerX=pressed.X; _drag.PointerY=pressed.Y;
            _drag.GrabX=pressed.X-item.CenterX.Value; _drag.GrabY=pressed.Y-item.CenterY.Value;
            _dragPins=_pins.ToArray(); _previewPins=null; _pointerX=double.NaN;
            item.Button.Opacity=.8;
            try { DragDrop.DoDragDrop(button,new DataObject("GlassDock.PinId",current.Id),DragDropEffects.Move); }
            finally { _previewPins=null; _dragPins=null; _drag.End(); Rebuild(); StartAnimation(); }
        };
        button.ContextMenuOpening+=(_,_)=> {
            var contextApp=item.App!;
            var menu=button.ContextMenu!;
            menu.Items.Clear();
            var windows=WindowService.GetWindows(contextApp,_windows);
            foreach(var window in windows.Take(12)) { var captured=window; menu.Items.Add(Ui.Menu(window.Title,()=>WindowService.Activate(captured))); }
            if(windows.Count>0) menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Menu("打开新窗口",()=>WindowService.ActivateOrLaunch(contextApp,Array.Empty<WindowSnapshot>())));
            if(_pins.Any(p=>p.Id==contextApp.Id)) {
                menu.Items.Add(Ui.Menu("向左移动",()=>MovePin(contextApp,-1))); menu.Items.Add(Ui.Menu("向右移动",()=>MovePin(contextApp,1)));
                menu.Items.Add(Ui.Menu("从 Dock 移除",()=> { _pins.RemoveAll(p=>p.Id==contextApp.Id); SavePins(); Rebuild(); }));
            } else menu.Items.Add(Ui.Menu("保留在 Dock",()=> { _pins.Add(contextApp); SavePins(); Rebuild(); }));
        };
        button.ContextMenu=new ContextMenu(); // creates a native WPF keyboard-accessible context menu
    }
    private void MovePin(PinnedApp app,int offset)
    {
        var index=_pins.FindIndex(p=>p.Id==app.Id); var next=Math.Clamp(index+offset,0,_pins.Count-1);
        if(index<0 || index==next) return; _pins.RemoveAt(index); _pins.Insert(next,app); SavePins(); Rebuild();
    }
    private void DragOverDock(object sender,DragEventArgs e)
    {
        bool external=e.Data.GetDataPresent(DataFormats.FileDrop),internalPin=e.Data.GetDataPresent("GlassDock.PinId");
        e.Effects=e.AllowedEffects&(external?DragDropEffects.Copy:internalPin?DragDropEffects.Move:DragDropEffects.None);
        if(e.Effects==DragDropEffects.None) { _drag.Leave(); _externalVisual.Leave(); StartAnimation(); e.Handled=true; return; }
        var pointer=e.GetPosition(Root); _pointerX=double.NaN;
        _drag.External=external; _drag.Over=true; _drag.PointerX=pointer.X; _drag.PointerY=pointer.Y;
        _drag.Proximity=DockLayout.Proximity(pointer.Y,81,78);
        if(external) { _externalVisual.Update(this,Root,e); _drag.Boundary=DockLayout.InsertionBoundary(pointer.X,Root.Width/2,_items.Count,Program.Prefs.IconSize+14); }
        else if(_drag.PinId is not null) PreviewPinOrder(pointer.X);
        StartAnimation(); e.Handled=true;
    }
    private void LeaveDock(object sender,DragEventArgs e)
    {
        var pointer=e.GetPosition(Root);
        if(e.KeyStates!=DragDropKeyStates.None && pointer.X>=Surface.Left-14 && pointer.X<=Surface.Right+14 && pointer.Y>=0 && pointer.Y<=Root.Height) return;
        _drag.Leave();
        _externalVisual.Leave();
        if(_previewPins is not null) { _previewPins=null; Rebuild(); }
        StartAnimation();
    }
    private void PreviewPinOrder(double pointerX)
    {
        if(_dragPins is null || _drag.PinId is not string id) return;
        var pins=_dragPins.ToList(); var moving=pins.FirstOrDefault(pin=>pin.Id==id);
        if(moving is null) return;
        pins.Remove(moving);
        var visible=_items.Skip(2).Select(item=>item.App).Where(app=>app is not null && app.Id!=id && pins.Any(pin=>pin.Id==app.Id)).Cast<PinnedApp>().ToArray();
        double stride=Program.Prefs.IconSize+14;
        double start=Root.Width/2-(_items.Count-1)*stride/2+2*stride;
        int index=visible.Length;
        for(int i=0;i<visible.Length;i++)
            if(pointerX<_items.First(item=>item.App?.Id==visible[i].Id).CenterX.Value) { index=i; break; }
        int insert=index<visible.Length?pins.FindIndex(pin=>pin.Id==visible[index].Id):Math.Min(pins.Count,_page+visible.Length);
        pins.Insert(Math.Clamp(insert,0,pins.Count),moving);
        string order=string.Join('|',pins.Select(pin=>pin.Id));
        if(_previewPins is not null && order==string.Join('|',_previewPins.Select(pin=>pin.Id))) return;
        _previewPins=pins; Rebuild();
    }
    private int PinInsertionIndex(int boundary)
    {
        foreach(var item in _items.Skip(Math.Clamp(boundary,2,_items.Count)))
        {
            int index=_pins.FindIndex(pin=>pin.Id==item.App?.Id);
            if(index>=0) return index;
        }
        return _pins.Count;
    }
    private void DropDock(object sender,DragEventArgs e)
    {
        bool external=e.Data.GetDataPresent(DataFormats.FileDrop),internalPin=e.Data.GetDataPresent("GlassDock.PinId");
        var effect=e.AllowedEffects&(external?DragDropEffects.Copy:internalPin?DragDropEffects.Move:DragDropEffects.None);
        e.Effects=DragDropEffects.None;
        if(effect==DragDropEffects.None) { _drag.End(); _externalVisual.Leave(); StartAnimation(); e.Handled=true; return; }
        try
        {
            var point=e.GetPosition(Root);
            int boundary=DockLayout.InsertionBoundary(point.X,Root.Width/2,_items.Count,Program.Prefs.IconSize+14);
            int insert=PinInsertionIndex(boundary);
            if(e.Data.GetData("GlassDock.PinId") is string id)
            {
                if(_previewPins is not null) { _pins.Clear(); _pins.AddRange(_previewPins); }
                else { var oldIndex=_pins.FindIndex(p=>p.Id==id); if(oldIndex>=0) { var pin=_pins[oldIndex]; _pins.RemoveAt(oldIndex); if(oldIndex<insert) insert--; _pins.Insert(Math.Clamp(insert,0,_pins.Count),pin); } }
            }
            else if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                _entryPoint=point;
                int previousCount=_pins.Count;
                var additions=paths.Take(64).Select(_externalVisual.Resolve).ToArray();
                foreach(var pin in additions) if(!_pins.Any(p=>p.Identity==pin.Identity)) _pins.Insert(Math.Min(insert++,_pins.Count),pin);
                // The new icon consumes the reserved slot. Keeping both would briefly add
                // a second empty space during the settling animation.
                if(_pins.Count>previousCount) _insertionGap.Snap(0);
            }
            _previewPins=null; _drag.End(); SavePins(); Rebuild(); StartAnimation();
            e.Effects=effect;
        } catch(Exception ex) { Program.Notify("无法添加项目："+ex.Message); }
        finally { if(external) _externalVisual.Drop(Root,e); _drag.Leave(); _entryPoint=null; StartAnimation(); }
        e.Handled=true;
    }
    private void AddWithDialog()
    {
        var dialog=new Microsoft.Win32.OpenFileDialog { Title="选择应用或快捷方式",Filter="应用与快捷方式|*.exe;*.lnk;*.url|所有文件|*.*",Multiselect=true };
        if(dialog.ShowDialog()==true) { foreach(var p in dialog.FileNames) { var pin=PinStore.CreatePin(p); if(!_pins.Any(a=>a.Identity==pin.Identity)) _pins.Add(pin); } SavePins(); Rebuild(); }
    }
    private void StartAnimation()
    {
        if(_animating) return; _animating=true; _lastTime=0; CompositionTarget.Rendering+=Animate;
    }
    internal void PreviewMagnification()
    {
        double stride=Program.Prefs.IconSize+14;
        _pointerX=(Root.Width-_items.Count*stride)/2+(Math.Min(3,_items.Count-1)+.5)*stride;
        StartAnimation();
    }
    internal void PreviewPointer(double? fraction)
    {
        double stride=Program.Prefs.IconSize+14;
        _pointerX=fraction is double value?Root.Width/2+(_items.Count-1)*stride*(Math.Clamp(value,0,1)-.5):double.NaN;
        StartAnimation();
    }
    internal void PreviewDragInsertion(double proximity=.9)
    {
        _pointerX=double.NaN;
        _drag.External=true; _drag.Over=true; _drag.PinId=null;
        _drag.Boundary=Math.Clamp(3,2,_items.Count);
        _drag.Proximity=Math.Clamp(proximity,0,1);
        StartAnimation();
    }
    internal void PreviewEndDrag()
    {
        _drag.End(); _previewPins=null;
        _pointerX=double.NaN; StartAnimation();
    }
    internal object InspectLayout()=>new
    {
        ItemCount=_items.Count,
        MaterialWidth=_materialWidth.Value,
        BaseWidth=_baseWidthTarget,
        Gap=_insertionGap.Value,
        GapTarget=_drag.External && _drag.Over?(Program.Prefs.IconSize+14)*_drag.Proximity:0,
        InsertionBoundary=_drag.Boundary,
        DragActive=_drag.Over,
        Animating=_animating,
        DesktopSuspended=_desktopSuspended,
        Scale=ContentScale,
        Layer=_layer?.Inspect(),
        RunningBoundary=_runningBoundary,
        Applications=_items.Where(item=>item.App is not null).Select(item=>new { item.App!.Name,item.App.AppUserModelId,Running=WindowService.GetWindows(item.App,_windows).Count>0 }).ToArray(),
        WindowTop=Top,WindowHeight=Height,
        RunningDots=0,
        VerticalPadding=(Surface.Height-(Program.Prefs.IconSize+10))/2,
        SurfacePhysical=PresentationSource.FromVisual(Root) is not null?new { Left=Root.PointToScreen(Surface.TopLeft).X,Right=Root.PointToScreen(Surface.BottomRight).X,Top=Root.PointToScreen(Surface.TopLeft).Y,Bottom=Root.PointToScreen(Surface.BottomRight).Y }:null,
        IconsContained=_items.All(item=>item.CenterX.Value-(Program.Prefs.IconSize+10)*item.Scale.Value/2>=Surface.Left-.1
            && item.CenterX.Value+(Program.Prefs.IconSize+10)*item.Scale.Value/2<=Surface.Right+.1),
        Centers=_items.Select(item=>new { X=item.CenterX.Value,Y=item.CenterY.Value,Scale=item.Scale.Value }).ToArray()
    };
    private void StopAnimation() { CompositionTarget.Rendering-=Animate; _animating=false; }
    private void Animate(object? sender,EventArgs e)
    {
        if(e is not RenderingEventArgs args) return;
        double now=args.RenderingTime.TotalSeconds; double dt=_lastTime==0?1d/60:Math.Clamp(now-_lastTime,0,.033); _lastTime=now;
        if(!LayoutFrame(dt,Program.Prefs.ReducedMotion)) StopAnimation();
    }
    private bool LayoutFrame(double elapsed,bool immediate)
    {
        double size=Program.Prefs.IconSize,stride=size+14;
        bool moving=false;
        double gapTarget=_drag.External && _drag.Over?stride*_drag.Proximity:0;
        if(immediate) _insertionGap.Snap(gapTarget);
        else moving|=_insertionGap.Advance(gapTarget,elapsed,.28);
        double surfaceHeight=size+22;
        Span<double> scales=_items.Count<=128?stackalloc double[_items.Count]:new double[_items.Count];
        Span<double> centers=_items.Count<=128?stackalloc double[_items.Count]:new double[_items.Count];
        double start=Root.Width/2-_items.Count*stride/2;
        for(int i=0;i<_items.Count;i++)
        {
            var item=_items[i];
            double distance=double.IsNaN(_pointerX)?999:Math.Abs(_pointerX-(start+(i+.5)*stride));
            double weight=DockLayout.Influence(distance);
            double scale=1+(immediate || _drag.Over?0:Program.Prefs.Magnification*weight);
            if(immediate) item.Scale.Snap(scale);
            else moving|=item.Scale.Advance(scale,elapsed,.30,.0005);
            scales[i]=item.Scale.Value;
        }
        DockLayout.Centers(centers,scales,Root.Width/2,size,_drag.Boundary,_insertionGap.Value);
        double baseline=159-surfaceHeight/2;
        for(int i=0;i<_items.Count;i++)
        {
            var item=_items[i];
            if(!item.Positioned) { item.CenterX.Snap(centers[i]); item.CenterY.Snap(baseline); item.Positioned=true; }
            bool dragged=_drag.Over && !_drag.External && item.App?.Id==_drag.PinId;
            if(dragged)
            {
                item.CenterX.Follow(_drag.PointerX-_drag.GrabX,elapsed);
                item.CenterY.Follow(Math.Clamp(_drag.PointerY-_drag.GrabY,size*.65,baseline+5),elapsed);
            }
            else if(immediate) { item.CenterX.Snap(centers[i]); item.CenterY.Snap(baseline); }
            else { moving|=item.CenterX.Advance(centers[i],elapsed,.28); moving|=item.CenterY.Advance(baseline,elapsed,.28); }
            if(immediate) item.Opacity.Snap(1); else moving|=item.Opacity.Advance(1,elapsed,.22,.005);
            centers[i]=item.CenterX.Value;
        }
        // Enclose the presentation positions, including sorting springs and the
        // scaled button footprint. Growth cannot lag behind the icons it contains.
        double enclosing=DockLayout.EnclosingWidth(centers,scales,Root.Width/2,size);
        double widthTarget=Math.Min(Root.Width-18,Math.Max(_baseWidthTarget+gapTarget,enclosing));
        if(immediate) _materialWidth.Snap(widthTarget);
        else moving|=_materialWidth.Advance(widthTarget,elapsed,.28);
        _materialWidth.Value=Math.Max(_materialWidth.Value,Math.Min(Root.Width-18,enclosing));
        double width=Math.Clamp(_materialWidth.Value,4,Math.Max(4,Root.Width-18));
        var surface=new Rect((Root.Width-width)/2,159-surfaceHeight,width,surfaceHeight);
        if(Math.Abs(Surface.Left-surface.Left)>.01 || Math.Abs(Surface.Width-surface.Width)>.01
            || Math.Abs(Surface.Top-surface.Top)>.01 || Math.Abs(Surface.Height-surface.Height)>.01) SetSurface(surface,25);
        _approach.Width=width+28; _approach.Height=164;
        Canvas.SetLeft(_approach,surface.Left-14); Canvas.SetTop(_approach,0);
        for(int i=0;i<_items.Count;i++)
        {
            var item=_items[i];
            bool dragged=_drag.Over && !_drag.External && item.App?.Id==_drag.PinId;
            item.Position.X=item.CenterX.Value-Surface.Left-(size+10)/2;
            item.Position.Y=item.CenterY.Value-Surface.Top-(size+10)/2;
            item.Scaling.ScaleX=item.Scale.Value; item.Scaling.ScaleY=item.Scale.Value;
            item.Button.Opacity=dragged ? .82 : item.Opacity.Value;
            Panel.SetZIndex(item.Button,dragged?10:1);
        }
        bool dividerVisible=_runningBoundary>0 && _runningBoundary<_items.Count;
        _runningDivider.Visibility=dividerVisible?Visibility.Visible:Visibility.Collapsed;
        if(dividerVisible)
        {
            double dividerX=(_items[_runningBoundary-1].CenterX.Value+_items[_runningBoundary].CenterX.Value)/2;
            Canvas.SetLeft(_runningDivider,dividerX-Surface.Left-_runningDivider.Width/2);
            Canvas.SetTop(_runningDivider,(Surface.Height-_runningDivider.Height)/2);
        }
        return moving;
    }
    private sealed class DockItem(string key,Button button,PinnedApp? app,Grid content,FrameworkElement visual)
    {
        public string Key=key;
        public Button Button=button; public PinnedApp? App=app;
        public Grid Content=content;
        public FrameworkElement Visual=visual;
        public bool Positioned;
        public DockSpring Scale=new(1),CenterX=new(),CenterY=new(),Opacity=new(1);
        public ScaleTransform Scaling=new(1,1);
        public TranslateTransform Position=new();
        public Border ActiveFrame=new();
        public TextBlock? GlyphText;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
