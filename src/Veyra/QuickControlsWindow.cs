using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDock.Services;
using GlassDock.Shell;

namespace GlassDock;

internal sealed class QuickControlsWindow : GlassWindow
{
    private static readonly Brush Secondary=new SolidColorBrush(Color.FromRgb(62,74,89));
    private readonly SidebarWindow _sidebar;
    private readonly nint _inputTarget;
    private readonly StackPanel _body=new() { Margin=new Thickness(0,6,0,0) };
    private readonly TextBlock _heading=Ui.Text("控制中心",20);
    private readonly TextBlock _status=Ui.Text("",12,Secondary);
    private readonly AudioService _audio=new();
    private readonly BrightnessService _brightness;
    private readonly Dictionary<ControlSection, Button> _tabs = [];
    private TrayPanel? _trayPanel;
    private readonly List<GlassCard> _cards=[];
    private Brush _chromeInk=Ui.Ink;
    private bool _darkBackground,_contrastInitialized;
    private readonly DispatcherTimer _audioDelay=new() { Interval=TimeSpan.FromMilliseconds(90) };
    private double _pendingVolume;
    private bool _volumePending;
    private int _generation;
    private bool _closed;
    private bool _sectionReady;
    private double _preferredHeight=420;
    private ControlSection _section;
    private WifiSnapshot? _wifiSnapshot;
    internal ControlSection CurrentSection=>_section;
    internal bool SectionReady=>_sectionReady;
    protected override bool NoActivate=>false;
    public QuickControlsWindow(SidebarWindow sidebar,nint inputTarget) : base("Veyra · 控制中心")
    {
        _sidebar=sidebar; _inputTarget=inputTarget; _brightness=new BrightnessService(inputTarget);
        ShowActivated=true; Width=386; Height=_preferredHeight+10;
        var layout=new Grid { Margin=new Thickness(23,16,23,19) };
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        _heading.FontWeight=FontWeights.SemiBold; _heading.VerticalAlignment=VerticalAlignment.Center; layout.Children.Add(_heading);
        var tabs=new UniformGrid { Rows=1,Columns=4 };
        foreach(var pair in new[]{("控制",ControlSection.Overview),("网络",ControlSection.Wifi),("日历",ControlSection.Calendar),("应用",ControlSection.Apps)})
        { var tab=ActionButton(pair.Item1,pair.Item1,()=>ShowSection(pair.Item2),double.NaN,30); tab.Margin=new Thickness(1,0,1,0); tabs.Children.Add(tab); _tabs[pair.Item2]=tab; }
        var tabSurface=new GlassCard { CornerRadius=14,Tint=.02f,Margin=new Thickness(0,13,0,3),Content=new Border { Padding=new Thickness(3),Child=tabs } };
        tabSurface.LuminanceChanged+=value=> { _chromeInk=value<.48?Brushes.White:Ui.Ink; UpdateTabs(); };
        Grid.SetRow(tabSurface,1); layout.Children.Add(tabSurface);
        var scroll=new ScrollViewer { Content=_body,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,PanningMode=PanningMode.VerticalOnly }; Grid.SetRow(scroll,2); layout.Children.Add(scroll);
        _status.Margin=new Thickness(4,6,4,0); _status.MaxHeight=40; _status.Visibility=Visibility.Collapsed; Grid.SetRow(_status,3); layout.Children.Add(_status); ContentLayer.Children.Add(layout);
        Loaded+=(_,_)=> { PositionBeside(_sidebar); if(_sectionReady) FitToContent(); };
        Deactivated+=(_,_)=> { if(!Program.CapturePreview && IsVisible && Program.TrayMenus?.Active!=true && !_sidebar.IsRailInteraction) Close(); };
        PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Escape) { e.Handled=true; Close(); } };
        _audioDelay.Tick+=(_,_)=>CommitVolume();
        Closed+=(_,_)=> { CommitVolume(); _closed=true; _generation++; _audio.Dispose(); _brightness.Dispose(); _trayPanel?.Dispose(); };
    }
    public void PositionBeside(SidebarWindow sidebar)
    {
        if(!IsLoaded) return;
        var bounds=DesktopNative.MonitorRect(sidebar); var dpi=VisualTreeHelper.GetDpi(sidebar);
        Height=Math.Min(_preferredHeight,(bounds.Bottom-bounds.Top)/dpi.DpiScaleY-30)+10;
        Left=Math.Max(bounds.Left/dpi.DpiScaleX+5,sidebar.ExpandedSurfaceScreenBounds.Left/dpi.DpiScaleX-Width);
        // Align the visible glass edges. A taller page may move upward only to
        // remain on-screen; changing sections never drops its top below the rail.
        double alignedTop=sidebar.ExpandedSurfaceScreenBounds.Top/dpi.DpiScaleY-8;
        Top=Math.Clamp(alignedTop,bounds.Top/dpi.DpiScaleY+7,bounds.Bottom/dpi.DpiScaleY-Height-7);
        SetSurface(new Rect(8,8,Width-16,Height-16),27); RenderGlass();
    }
    internal object InspectLayout()
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        var surface=new Rect(Root.PointToScreen(Surface.TopLeft),Root.PointToScreen(Surface.BottomRight));
        var sidebar=_sidebar.ExpandedSurfaceScreenBounds;
        return new {
            DpiScaleX=dpi.DpiScaleX,DpiScaleY=dpi.DpiScaleY,
            WindowDip=Bounds(new Rect(Left,Top,Width,Height)),
            WindowPhysical=Bounds(new Rect(PointToScreen(new Point()),PointToScreen(new Point(Width,Height)))),
            SurfacePhysical=Bounds(surface),SidebarSurfacePhysical=Bounds(sidebar),
            MaterialPhysical=Bounds(new Rect(Glass.PointToScreen(new Point()),Glass.PointToScreen(new Point(Glass.Width,Glass.Height)))),
            SurfaceTopDeltaPhysical=surface.Top-sidebar.Top,
            PreferredHeight=_preferredHeight,ContentHeight=_body.DesiredSize.Height,
            Section=_section.ToString(),SectionReady=_sectionReady,CardCount=_cards.Count,WifiAvailable=_wifiSnapshot?.IsAvailable,NetworkCount=_wifiSnapshot?.Networks.Count,
            Tray=_trayPanel?.InspectLayout()
        };
    }
    private static object Bounds(Rect rect)=>new { X=rect.X,Y=rect.Y,Width=rect.Width,Height=rect.Height };
    private void FitToContent()
    {
        if(_closed || !IsLoaded) return;
        _body.Measure(new Size(Math.Max(1,Width-62),double.PositiveInfinity));
        _status.Measure(new Size(Math.Max(1,Width-70),double.PositiveInfinity));
        double statusHeight=_status.Visibility==Visibility.Visible?_status.DesiredSize.Height:0;
        _preferredHeight=Math.Clamp(123+_body.DesiredSize.Height+statusHeight,280,620);
        PositionBeside(_sidebar);
    }
    protected override void Reposition()=>PositionBeside(_sidebar);
    public override void ApplyPreferences()
    {
        base.ApplyPreferences();
        if(!Program.Prefs.GlassEnabled) OnMaterialLuminance(.85);
    }
    protected override void OnMaterialLuminance(double luminance)
    {
        bool dark=luminance<.48;
        if(_contrastInitialized && dark==_darkBackground) return;
        _contrastInitialized=true; _darkBackground=dark; _chromeInk=dark?Brushes.White:Ui.Ink;
        _heading.Foreground=_chromeInk; _status.Foreground=_chromeInk;
        UpdateTabs();
    }
    private void UpdateTabs()
    {
        var selected=_section is ControlSection.Audio or ControlSection.Power?ControlSection.Overview:_section;
        foreach(var pair in _tabs)
        {
            bool active=pair.Key==selected;
            pair.Value.Background=new SolidColorBrush(active?Color.FromArgb(45,255,255,255):Colors.Transparent);
            if(pair.Value.Content is TextBlock text) { text.Foreground=_chromeInk; text.FontWeight=active?FontWeights.SemiBold:FontWeights.Normal; }
        }
    }
    public async void ShowSection(ControlSection section)=>await ShowSectionAsync(section);
    private async Task ShowSectionAsync(ControlSection section)
    {
        if(_closed) return;
        CommitVolume(); _section=section; _sectionReady=false; int generation=++_generation;
        _heading.Text=section==ControlSection.Wifi?"网络":"控制中心";
        foreach(var card in _cards) card.Dispose();
        _body.Children.Clear(); _cards.Clear(); Status("");
        _trayPanel?.Dispose(); _trayPanel=null;
        UpdateTabs();
        try
        {
            switch(section)
            {
                case ControlSection.Wifi: await BuildWifi(generation); break;
                case ControlSection.Calendar: BuildCalendar(); break;
                case ControlSection.Apps: BuildApps(); break;
                case ControlSection.Power: BuildPower(); await AddBrightness(generation); break;
                case ControlSection.Audio: BuildAudio(); await AddBrightness(generation); break;
                default:
                    BuildAudio();
                    var radioCard=Card("无线连接");
                    var wifiTask=WifiService.GetSnapshotAsync(); var bluetoothTask=RadioService.ReadBluetoothAsync(); var brightnessTask=AddBrightness(generation);
                    var wifi=await wifiTask; var bluetooth=await bluetoothTask;
                    if(!Current(generation)) return;
                    string wifiDetail=wifi.HardwareBlocked?"硬件已关闭":wifi.RadioEnabled switch { true=>"已开启",false=>"已关闭",null=>"状态未知" };
                    var wifiButton=RadioButton("\uE701","Wi-Fi",wifiDetail,wifi.RadioEnabled==true,()=>Run(async()=> { var result=await WifiService.SetRadioAsync(wifi.RadioEnabled!=true); if(Current(generation)) { await ShowSectionAsync(ControlSection.Overview); Status(result.Message); } })); wifiButton.IsEnabled=wifi.IsAvailable && !wifi.HardwareBlocked;
                    var bluetoothButton=RadioButton("\uE702","蓝牙",bluetooth.Available?(bluetooth.Enabled?"已开启":"已关闭"):"不可用",bluetooth.Enabled,()=>Run(async()=> { var state=await RadioService.SetBluetoothAsync(!bluetooth.Enabled); if(Current(generation)) { await ShowSectionAsync(ControlSection.Overview); Status(state.Error??(state.Enabled?"蓝牙已开启":"蓝牙已关闭")); } })); bluetoothButton.IsEnabled=bluetooth.Available;
                    if(wifi.Error is not null) wifiButton.ToolTip=wifi.Error;
                    if(bluetooth.Error is not null) bluetoothButton.ToolTip=bluetooth.Error;
                    ToolTipService.SetShowOnDisabled(wifiButton,true); ToolTipService.SetShowOnDisabled(bluetoothButton,true);
                    var connections=new Grid { Margin=new Thickness(0,5,0,0) };
                    connections.ColumnDefinitions.Add(new ColumnDefinition());
                    connections.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(6) });
                    connections.ColumnDefinitions.Add(new ColumnDefinition());
                    connections.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
                    connections.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
                    connections.Children.Add(wifiButton); Grid.SetColumn(bluetoothButton,2); connections.Children.Add(bluetoothButton);
                    var networkCaption=Ui.Text("更多网络  ›",12); networkCaption.Margin=new Thickness(4,0,0,0); networkCaption.HorizontalAlignment=HorizontalAlignment.Left; networkCaption.VerticalAlignment=VerticalAlignment.Center;
                    var networks=MaterialButton(networkCaption,"Wi-Fi 列表",()=>ShowSection(ControlSection.Wifi),double.NaN,24); networks.Margin=new Thickness(0,3,0,0);
                    Grid.SetRow(networks,1); connections.Children.Add(networks); radioCard.Children.Add(connections);
                    await brightnessTask;
                    break;
            }
        }
        catch(Exception ex) { if(Current(generation)) Status(ex.Message); }
        finally
        {
            if(Current(generation))
            {
                _sectionReady=true;
                _=Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=> { if(Current(generation)) FitToContent(); }));
            }
        }
    }
    private bool Current(int generation)=>!_closed && generation==_generation;
    private void Status(string? text)
    {
        if(_closed) return;
        _status.ToolTip=text;
        _status.Text=text is { Length:>24 }?text[..24]+"…":text??"";
        _status.Visibility=string.IsNullOrWhiteSpace(text)?Visibility.Collapsed:Visibility.Visible;
    }
    private async void Run(Func<Task> action) { try { await action(); } catch(Exception ex) { Status(ex.Message); } }
    private void CommitVolume()
    {
        _audioDelay.Stop(); if(!_volumePending) return; _volumePending=false;
        var result=_audio.SetVolume(_pendingVolume); _sidebar.ObserveAudioState(result); if(!result.Available) Status(result.Error);
    }
    private StackPanel Card(string title,FrameworkElement? trailing=null)
    {
        var stack=new StackPanel { Margin=new Thickness(12,9,12,9) };
        var header=new DockPanel();
        if(trailing is not null) { trailing.VerticalAlignment=VerticalAlignment.Center; DockPanel.SetDock(trailing,Dock.Right); header.Children.Add(trailing); }
        var heading=Ui.Text(title,14); heading.FontWeight=FontWeights.SemiBold; header.Children.Add(heading); stack.Children.Add(header);
        var surface=MaterialSurface(stack,20,.025f); surface.Margin=new Thickness(0,0,0,8); _body.Children.Add(surface); return stack;
    }
    private GlassCard MaterialSurface(FrameworkElement content,double radius,float tint)
    {
        var surface=new GlassCard { CornerRadius=radius,Tint=tint,Content=content };
        surface.LuminanceChanged+=luminance=>ApplyMaterialInk(content,luminance<.48?Brushes.White:Ui.Ink);
        _cards.Add(surface); return surface;
    }
    private static void ApplyMaterialInk(DependencyObject node,Brush ink)
    {
        if(node is GlassCard) return;
        if(node is TrayPanel trayPanel) { trayPanel.ApplyInk(ink); return; }
        if(node is GlassCalendar glassCalendar) { glassCalendar.ApplyInk(ink); return; }
        if(node is TextBlock text && text.Tag as string!="fixed") text.Foreground=ink;
        if(node is Calendar calendar) calendar.Foreground=ink;
        if(node is Button button) button.Foreground=ink;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) ApplyMaterialInk(VisualTreeHelper.GetChild(node,i),ink);
    }
    private Button MaterialButton(FrameworkElement content,string label,Action action,double width,double height)
    {
        var surface=MaterialSurface(new Border { Child=content },12,.025f); surface.Width=width; surface.Height=height;
        var button=Ui.Button(surface,label,action,width,height);
        if(double.IsNaN(width)) surface.SetBinding(FrameworkElement.WidthProperty,new Binding(nameof(ActualWidth)) { Source=button });
        return button;
    }
    private Button ActionButton(string text,string label,Action action,double width=132,double height=38)
    {
        var caption=Ui.Text(text,12); caption.HorizontalAlignment=HorizontalAlignment.Center; caption.VerticalAlignment=VerticalAlignment.Center;
        var button=double.IsNaN(width)?Ui.Button(caption,label,action,width,height):MaterialButton(caption,label,action,width,height);
        button.Background=Brushes.Transparent; button.Margin=new Thickness(0,5,6,0); return button;
    }
    private Button RadioButton(string glyph,string title,string detail,bool enabled,Action action)
    {
        var row=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(4,0,4,0) };
        var icon=Ui.Glyph(glyph,19); icon.Foreground=enabled?Brushes.White:Ui.Ink;
        if(enabled) icon.Tag="fixed";
        row.Children.Add(new Border { Width=32,Height=32,CornerRadius=new CornerRadius(16),Background=new SolidColorBrush(enabled?Color.FromArgb(100,79,111,144):Color.FromArgb(24,255,255,255)),Child=icon });
        var labels=new StackPanel { Margin=new Thickness(6,0,0,0),VerticalAlignment=VerticalAlignment.Center };
        var name=Ui.Text(title,12); name.FontWeight=FontWeights.SemiBold; labels.Children.Add(name); labels.Children.Add(Ui.Text(detail,10,Secondary)); row.Children.Add(labels);
        return MaterialButton(row,"切换"+title,action,double.NaN,46);
    }
    private static UnifiedSlider ValueSlider(double value,string label)
    {
        var slider=new UnifiedSlider { Minimum=0,Maximum=100,Value=value*100,ToolTip=label,Style=ControlStyles.Slider,SmallChange=1,LargeChange=10 };
        System.Windows.Automation.AutomationProperties.SetName(slider,label); return slider;
    }
    private void BuildAudio()
    {
        var state=_audio.Read(); var value=Ui.Text(state.Available?$"{Math.Round(state.Volume*100)}%":"不可用",12,Secondary); var card=Card("声音",value);
        _sidebar.ObserveAudioState(state);
        if(!state.Available) { card.ToolTip=state.Error; return; }
        var slider=ValueSlider(state.Volume,"系统主音量");
        slider.ValueChanged+=(_,_)=> { value.Text=$"{Math.Round(slider.Value)}%"; _pendingVolume=slider.Value/100; _volumePending=true; _audioDelay.Stop(); _audioDelay.Start(); };
        var material=MaterialSurface(slider,13,.02f); material.Margin=new Thickness(0,6,0,0); card.Children.Add(material);
        slider.ValueCommitted+=(_,_)=>CommitVolume();
    }
    private async Task AddBrightness(int generation)
    {
        var value=Ui.Text("—",12,Secondary); var card=Card("亮度",value);
        var state=await _brightness.ReadAsync(); if(!Current(generation)) return;
        if(!state.Available) { value.Text="不可用"; card.ToolTip=state.Error; return; }
        value.Text=$"{Math.Round(state.Brightness*100)}%";
        var slider=ValueSlider(state.Brightness,"显示器亮度");
        slider.ValueChanged+=(_,_)=>value.Text=$"{Math.Round(slider.Value)}%";
        double applied=state.Brightness; double? pending=null,inFlight=null; bool writing=false;
        async void Commit()
        {
            if(!Current(generation)) return;
            double requestedValue=slider.Value/100;
            if(writing && pending is null && inFlight is double current && Math.Abs(current-requestedValue)<.005) return;
            pending=requestedValue;
            if(writing) return;
            writing=true;
            try
            {
                while(pending is double requested && Current(generation))
                {
                    pending=null;
                    if(Math.Abs(requested-applied)<.005) continue;
                    inFlight=requested;
                    var result=await _brightness.SetBrightnessAsync(requested);
                    if(!Current(generation)) return;
                    if(!result.Available) { Status(result.Error); continue; }
                    applied=result.Brightness;
                    // Read the device after its write completes. An accepted API request can
                    // still be rounded or restricted by the display's firmware.
                    var observed=await _brightness.ReadAsync();
                    if(!Current(generation)) return;
                    if(observed.Available) applied=observed.Brightness;
                    if(pending is null) { slider.Value=applied*100; value.Text=$"{Math.Round(applied*100)}%"; }
                    Status(observed.Available?"显示器亮度已更新":observed.Error??"亮度请求已发送，暂时无法确认显示器状态");
                }
            }
            catch(Exception ex) { if(Current(generation)) Status(ex.Message); }
            finally { writing=false; inFlight=null; }
        }
        slider.ValueCommitted+=(_,_)=>Commit();
        var material=MaterialSurface(slider,13,.02f); material.Margin=new Thickness(0,6,0,0); card.Children.Add(material);
    }
    private async Task BuildWifi(int generation)
    {
        var state=Ui.Text("—",12,Secondary); var heading=Card("Wi-Fi",state);
        var snapshot=await WifiService.GetSnapshotAsync(); if(!Current(generation)) return;
        _wifiSnapshot=snapshot;
        heading.ToolTip=snapshot.Error;
        if(!snapshot.IsAvailable) { state.Text="不可用"; return; }
        state.Text=snapshot.HardwareBlocked?"硬件已关闭":snapshot.RadioEnabled switch { true=>"已开启",false=>"已关闭",null=>"状态未知" };
        var actions=new WrapPanel();
        var radio=ActionButton(snapshot.RadioEnabled==true?"关闭 Wi-Fi":"开启 Wi-Fi","切换 Wi-Fi",()=>Run(async()=> { var result=await WifiService.SetRadioAsync(snapshot.RadioEnabled!=true); if(Current(generation)) { await ShowSectionAsync(ControlSection.Wifi); Status(result.Message); } }),135,34); radio.IsEnabled=!snapshot.HardwareBlocked; actions.Children.Add(radio);
        actions.Children.Add(ActionButton("刷新","刷新网络",()=>Run(async()=> { await WifiService.GetSnapshotAsync(true); await Task.Delay(1700); if(Current(generation)) ShowSection(ControlSection.Wifi); }),135,34));
        heading.Children.Add(actions);
        if(snapshot.Networks.Count==0) heading.Children.Add(Ui.Text("暂无可用网络",12,Secondary));
        foreach(var network in snapshot.Networks)
        {
            var card=Card(network.Ssid);
            string detail=network.Connected?"已连接":network.IsInRange?$"信号 {network.Signal}% · {(network.SecurityKnown?(network.Secured?"加密":"开放"):"安全类型未知")}":"已保存 · 信号未确认";
            card.Children.Add(Ui.Text(detail,11,Secondary));
            var action=ActionButton(network.Connected?"断开连接":"连接","连接 "+network.Ssid,()=>Run(async()=> { var result=network.Connected?await WifiService.DisconnectAsync(network.InterfaceId):await WifiService.ConnectAsync(network); if(!Current(generation)) return; Status(result.Message); await Task.Delay(1200); if(Current(generation)) { await ShowSectionAsync(ControlSection.Wifi); Status(result.Message); } }),110,34);
            action.IsEnabled=network.Connected || network.Connectable && (network.HasProfile || !network.Secured); card.Children.Add(action);
            if(!action.IsEnabled) { action.ToolTip=!network.HasProfile && network.Secured?"此网络尚未保存，当前版本暂不支持首次密码配置":network.UnavailableReason; ToolTipService.SetShowOnDisabled(action,true); }
            if(!network.HasProfile && network.Secured) card.Children.Add(Ui.Text("需要首次配置",11,Secondary));
        }
    }
    private void BuildPower()
    {
        var state=PowerService.ReadBattery(); var card=Card("电池与供电");
        string charge=state.Percent is int percent?$"电量 {percent}%":"电量未知";
        card.Children.Add(Ui.Text(state.Present?$"{charge} · {(state.PluggedIn?"已接通电源":"使用电池")}":"使用外接电源",13,Secondary));
        foreach(var plan in PowerService.GetPlans()) card.Children.Add(ActionButton((plan.Active?"✓ ":"")+plan.Name,"切换电源计划 "+plan.Name,()=>Run(async()=> { PowerService.SetPlan(plan); bool confirmed=PowerService.GetPlans().Any(item=>item.Id==plan.Id && item.Active); await ShowSectionAsync(ControlSection.Power); Status(confirmed?"电源计划已切换":"电源计划请求已发送，当前活动计划尚未确认"); }),275,35));
    }
    private void BuildCalendar()
    {
        var calendar=ControlStyles.Calendar(); calendar.Margin=new Thickness(12,10,12,10);
        _body.Children.Add(MaterialSurface(calendar,20,.025f));
    }
    private void BuildApps()
    {
        _trayPanel=new TrayPanel(Program.TrayHost,Status,FitToContent);
        var traySurface=MaterialSurface(_trayPanel,20,.025f); traySurface.Margin=new Thickness(0,0,0,8); _body.Children.Add(traySurface);
        var executable=WallpaperService.FindRunningExecutable(); var wallpaper=Card("Wallpaper Engine",Ui.Text(executable is null?"未运行":"运行中",12,Secondary));
        if(executable is not null)
        {
            var row=new WrapPanel(); foreach(var command in new[]{("暂停","pause"),("播放","play"),("静音","mute"),("有声","unmute")}) row.Children.Add(ActionButton(command.Item1,"Wallpaper Engine "+command.Item1,()=> { WallpaperService.Control(command.Item2); Status("已发送 Wallpaper Engine "+command.Item1+"命令"); },125,36)); wallpaper.Children.Add(row);
        }
    }
}
