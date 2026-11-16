using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed class DesktopBoxGroupHandle : GlassWindow
{
    private readonly DesktopBoxGroupController _group;
    private readonly DesktopBoxManager _manager;
    private readonly StackPanel _content=new() { HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
    private readonly TextBlock _caption=Ui.Text("收纳",12);
    private readonly Grid _grip=new() { Width=78,Height=28,Background=Brushes.Transparent };
    private readonly Border _tab=new() { Background=new SolidColorBrush(Color.FromArgb(150,212,231,246)),CornerRadius=new CornerRadius(3),Cursor=Cursors.Hand,ToolTip="收纳 · 悬停展开" };
    private readonly Button _pin;
    internal bool Compact { get; private set; }
    internal void SuspendDesktop() { Hide(); SurfaceVisible=false; ReleaseMaterial(true); }
    internal Rect FullGlassBoundsPixels
    {
        get
        {
            var dpi=VisualTreeHelper.GetDpi(this); bool horizontal=_group.Edge is "Top" or "Free";
            double fullWidth=horizontal?132:52,fullHeight=horizontal?46:90;
            double x=Left,y=Top;
            if(Compact)
            {
                if(horizontal)x-=(fullWidth-Width)/2;
                else { y-=(fullHeight-Height)/2; if(_group.Edge=="Right")x-=fullWidth-Width; }
                if(_group.Edge=="Free")y-=(fullHeight-Height)/2;
            }
            return new Rect((x+6)*dpi.DpiScaleX,(y+6)*dpi.DpiScaleY,(fullWidth-12)*dpi.DpiScaleX,(fullHeight-12)*dpi.DpiScaleY);
        }
    }
    internal bool MenuOpen=>ContentLayer.ContextMenu?.IsOpen==true;
    internal Rect ScreenBounds=>new(PointToScreen(new Point()),PointToScreen(new Point(ActualWidth,ActualHeight)));
    internal DesktopBoxGroupHandle(DesktopBoxGroupController group,DesktopBoxManager manager):base("uilicd · 收纳组")
    {
        _group=group; _manager=manager; Topmost=false;
        _pin=Ui.Button(Ui.Glyph("\uE718",15),"固定全部 / 自动隐藏",group.TogglePinned,28,28);
        _caption.HorizontalAlignment=HorizontalAlignment.Center; _caption.VerticalAlignment=VerticalAlignment.Center;
        _grip.Children.Add(_caption);
        var thumb=new Thumb { Background=Brushes.Transparent,Cursor=Cursors.SizeAll,ToolTip="拖动总收纳" };
        var template=new ControlTemplate(typeof(Thumb)); var surface=new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty,Brushes.Transparent); template.VisualTree=surface; thumb.Template=template;
        _grip.Children.Add(thumb); thumb.DragStarted+=group.BeginHandleDrag; thumb.DragDelta+=group.MoveHandleDrag; thumb.DragCompleted+=group.EndHandleDrag;
        thumb.MouseDoubleClick+=(_,e)=> { group.ToggleVisibility(); e.Handled=true; };
        _content.Children.Add(_grip); _content.Children.Add(_pin); ContentLayer.Children.Add(_content);
        ContentLayer.Background=Brushes.Transparent; Root.Children.Add(_tab);
        ContentLayer.MouseEnter+=(_,_)=>group.EnterHandle(); ContentLayer.MouseLeave+=(_,_)=>group.LeaveHandle();
        _tab.MouseEnter+=(_,_)=>group.EnterHandle(); _tab.MouseLeave+=(_,_)=>group.LeaveHandle();
        ContentLayer.AllowDrop=true; ContentLayer.DragEnter+=(_,e)=> { group.Show(); e.Effects=DragDropEffects.None; e.Handled=true; };
        var menu=new ContextMenu();
        menu.Items.Add(Ui.Menu("新建映射盒…",manager.PromptCreateBox)); menu.Items.Add(Ui.Menu("管理收纳盒…",manager.ShowSettings));
        menu.Items.Add(Ui.Menu("全部展开",()=>group.SetCollapsed(false))); menu.Items.Add(Ui.Menu("全部收起",()=>group.SetCollapsed(true)));
        var edge=new MenuItem { Header="停靠位置" };
        foreach(var pair in new[]{("顶部","Top"),("左侧","Left"),("右侧","Right")})edge.Items.Add(Ui.Menu(pair.Item1,()=>group.SetEdge(pair.Item2),true));
        menu.Opened+=(_,_)=> { group.Show(); for(int index=0;index<edge.Items.Count;index++)((MenuItem)edge.Items[index]).IsChecked=group.Edge==new[]{"Top","Left","Right"}[index]; };
        menu.Items.Add(edge); menu.Items.Add(new Separator()); menu.Items.Add(Ui.Menu("固定全部 / 自动隐藏",group.TogglePinned));
        ContentLayer.ContextMenu=menu; Root.ContextMenu=menu; Loaded+=(_,_)=>Refresh();
    }
    internal void SetCompact(bool compact) { if(Compact==compact)return; Compact=compact; Refresh(); }
    internal void Refresh()
    {
        if(_group.Dragging)return;
        bool horizontal=_group.Edge is "Top" or "Free";
        _content.Orientation=horizontal?Orientation.Horizontal:Orientation.Vertical; _grip.Width=horizontal?78:32;
        _caption.Text=horizontal?"收纳 · "+_manager.DesktopWindows.Count():_manager.DesktopWindows.Count().ToString();
        _pin.Opacity=_group.Pinned?1:.45;
        _pin.ToolTip=_group.Pinned?"固定全部 · 点击自动隐藏":"自动隐藏 · 点击固定全部";
        Width=Compact?(horizontal?62:16):(horizontal?132:52);
        Height=Compact?(horizontal?16:62):(horizontal?46:90); Reposition();
    }
    internal void MoveControlPixels(Point origin)
    {
        var dpi=VisualTreeHelper.GetDpi(this); Left=origin.X/dpi.DpiScaleX; Top=origin.Y/dpi.DpiScaleY;
    }
    protected override void Reposition()
    {
        if(!IsLoaded || _group.Dragging)return;
        var monitor=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
        bool horizontal=_group.Edge is "Top" or "Free";
        double fullWidth=horizontal?132:52,fullHeight=horizontal?46:90;
        double x=_manager.Configuration.GroupLeft??monitor.Right/dpi.DpiScaleX-fullWidth-90;
        double y=_manager.Configuration.GroupTop??monitor.Top/dpi.DpiScaleY;
        if(_group.Edge=="Top")y=monitor.Top/dpi.DpiScaleY;
        else if(_group.Edge is "Left" or "Right")
        {
            x=_group.Edge=="Left"?monitor.Left/dpi.DpiScaleX:monitor.Right/dpi.DpiScaleX-fullWidth;
            y=_manager.Configuration.GroupTop??(_group.Edge=="Right"?monitor.Top/dpi.DpiScaleY+48:(monitor.Top+(monitor.Bottom-monitor.Top-fullHeight*dpi.DpiScaleY)/2)/dpi.DpiScaleY);
        }
        x=Math.Clamp(x,monitor.Left/dpi.DpiScaleX,Math.Max(monitor.Left/dpi.DpiScaleX,monitor.Right/dpi.DpiScaleX-fullWidth));
        y=Math.Clamp(y,monitor.Top/dpi.DpiScaleY,Math.Max(monitor.Top/dpi.DpiScaleY,monitor.Bottom/dpi.DpiScaleY-fullHeight));
        if(Compact)
        {
            if(horizontal)x+=(fullWidth-Width)/2;
            else { y+=(fullHeight-Height)/2; if(_group.Edge=="Right")x+=fullWidth-Width; }
            if(_group.Edge=="Free")y+=(fullHeight-Height)/2;
        }
        Left=x; Top=y;
        SetSurface(new Rect(6,6,Width-12,Height-12),Compact?2:16);
        _content.Visibility=Compact?Visibility.Collapsed:Visibility.Visible;
        _tab.Visibility=Compact?Visibility.Visible:Visibility.Collapsed;
        _tab.Width=horizontal?50:4; _tab.Height=horizontal?4:50;
        Canvas.SetLeft(_tab,6); Canvas.SetTop(_tab,6);
        if(Compact) { SurfaceVisible=false; ReleaseMaterial(true); Glass.Visibility=Fallback.Visibility=Visibility.Collapsed; }
        else { Glass.Visibility=Visibility.Visible; Fallback.Visibility=Glass.Source is null?Visibility.Visible:Visibility.Hidden; UpdateDesktopLayer(); RenderGlass(); }
    }
    internal void UpdateDesktopLayer()
    {
        if(!IsLoaded || !IsVisible || _group.Dragging)return;
        DesktopNative.PlaceFloatingDesktopSurface(this);
        bool visible=!Compact && !DesktopNative.ForegroundCoversMonitor(this);
        if(SurfaceVisible==visible)return; SurfaceVisible=visible; if(visible)RenderGlass(); else ReleaseMaterial();
    }
    protected override void OnMaterialLuminance(double value)
    {
        var ink=value<.48?Brushes.White:Ui.Ink;
        void Apply(DependencyObject node) { if(node is TextBlock text)text.Foreground=ink; for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)Apply(VisualTreeHelper.GetChild(node,i)); }
        Apply(_content);
    }
}
