using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDock.Shell;

/// <summary>Notification-area presentation, independent of the control-center pages.</summary>
internal sealed class TrayPanel : StackPanel,IDisposable
{
    private const double IconSize=16,TargetSize=32;
    private static readonly HashSet<Guid> DirectSystemModules=[
        new("7820ae73-23e3-4229-82c1-e41cb67d5b9c"),
        new("7820ae74-23e3-4229-82c1-e41cb67d5b9c"),
        new("7820ae75-23e3-4229-82c1-e41cb67d5b9c")];
    private readonly TrayHostService? _host;
    private readonly Action<string> _status;
    private readonly Action _layout;
    private readonly WrapPanel _items=new() { ItemWidth=TargetSize,ItemHeight=TargetSize,Margin=new Thickness(0,5,0,0),UseLayoutRounding=true,SnapsToDevicePixels=true };
    private readonly TextBlock _count=Ui.Text("",12);
    private readonly TextBlock _heading=Ui.Text("隐藏托盘",14);
    private readonly DispatcherTimer _refresh=new() { Interval=TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<string,(Button Button,Image Image)> _buttons=[];
    private readonly Dictionary<string,Point> _anchors=[];
    private bool _disposed;
    internal TrayPanel(TrayHostService? host,Action<string> status,Action layout)
    {
        _host=host; _status=status; _layout=layout;
        Margin=new Thickness(12,9,12,9);
        var header=new DockPanel();
        var refresh=Ui.Button(Ui.Glyph("\uE72C",13),"刷新托盘",()=>_host?.RequestRefresh(),25,23);
        DockPanel.SetDock(refresh,Dock.Right); header.Children.Add(refresh);
        _count.VerticalAlignment=VerticalAlignment.Center; _count.Margin=new Thickness(0,0,6,0); DockPanel.SetDock(_count,Dock.Right); header.Children.Add(_count);
        _heading.FontWeight=FontWeights.SemiBold; header.Children.Add(_heading); Children.Add(header); Children.Add(_items);
        _refresh.Tick+=(_,_)=> { _refresh.Stop(); Refresh(); };
        if(host is not null) host.Changed+=OnChanged;
        Unloaded+=(_,_)=>Dispose();
        Refresh();
    }
    internal void ApplyInk(Brush ink) { _heading.Foreground=ink; _count.Foreground=ink; }
    private void OnChanged(object? sender,EventArgs e) { if(!_disposed && !_refresh.IsEnabled) _refresh.Start(); }
    private void Refresh()
    {
        if(_disposed) return;
        var all=_host?.Snapshot??[];
        var icons=all.Where(icon=>!icon.Hidden && (icon.Image is not null || icon.HasVisualRegistration) && icon.CallbackMessage>=0x400 && !DirectSystemModules.Contains(icon.Guid)).ToArray();
        _count.Text=_host?.IsRunning==true?$"{icons.Length}":"—";
        _count.ToolTip=all.Any(icon=>!icon.Hidden && ((!icon.HasVisualRegistration && icon.Image is null) || icon.CallbackMessage<0x400))?"部分应用尚未重新注册完整图标":"";
        var keys=icons.Select(icon=>icon.Key).ToHashSet(StringComparer.Ordinal);
        bool rebuild=!keys.SetEquals(_buttons.Keys);
        foreach(var stale in _buttons.Keys.Where(key=>!keys.Contains(key)).ToArray()) {_items.Children.Remove(_buttons[stale].Button); _buttons.Remove(stale); _anchors.Remove(stale);}
        foreach(var icon in icons)
        {
            if(!_buttons.TryGetValue(icon.Key,out var entry))
            {
                string key=icon.Key; bool doubleClick=false;
                var image=new Image { Width=IconSize,Height=IconSize,Stretch=Stretch.Uniform,UseLayoutRounding=true,SnapsToDevicePixels=true };
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                // The hit target already supplies spacing around the native 16-DIP
                // icon. Extra margins wasted a full column in the standard panel.
                var button=Ui.Button(image,"",()=>{},TargetSize,TargetSize);
                button.UseLayoutRounding=true; button.SnapsToDevicePixels=true;
                button.PreviewMouseLeftButtonDown+=(_,e)=> { if(e.ClickCount==2) { doubleClick=true; Invoke(key,TrayClick.Double,button); e.Handled=true; } };
                button.PreviewMouseLeftButtonUp+=(_,e)=> { if(doubleClick) { doubleClick=false; e.Handled=true; } };
                button.Click+=(_,_)=>Invoke(key,TrayClick.Primary,button);
                button.PreviewMouseRightButtonUp+=(_,e)=> { Invoke(key,TrayClick.Secondary,button); e.Handled=true; };
                button.PreviewMouseUp+=(_,e)=> { if(e.ChangedButton==MouseButton.Middle) { Invoke(key,TrayClick.Middle,button); e.Handled=true; } };
                button.Loaded+=(_,_)=>Place(key,button);
                button.MouseEnter+=(_,_)=> { if(!_disposed && TryPlace(key,button,out var point)) _host?.Hover(key,true,point); };
                // WPF raises MouseLeave while disposing HwndMouseInputProvider. IsLoaded
                // may still be true although PresentationSource has already detached.
                button.MouseLeave+=(_,_)=> { doubleClick=false; if(!_disposed && _anchors.TryGetValue(key,out var point)) _host?.Hover(key,false,point); };
                button.PreviewKeyDown+=(_,e)=> {
                    if(e.Key is Key.Return or Key.Space) { Invoke(key,TrayClick.KeyboardPrimary,button); e.Handled=true; }
                    else if(e.Key==Key.Apps || e.Key==Key.F10 && Keyboard.Modifiers==ModifierKeys.Shift) { Invoke(key,TrayClick.Secondary,button); e.Handled=true; }
                };
                _items.Children.Add(button); _buttons[key]=entry=(button,image);
            }
            entry.Image.Source=icon.Image;
            string name=string.IsNullOrWhiteSpace(icon.Tooltip)?Path.GetFileNameWithoutExtension(icon.OwnerPath):icon.Tooltip;
            entry.Button.ToolTip=name; System.Windows.Automation.AutomationProperties.SetName(entry.Button,name);
            Place(icon.Key,entry.Button);
        }
        if(rebuild && IsLoaded) _layout();
    }
    internal void RefreshForProbe()=>Refresh();
    internal (string Key,Button Button,Image Image)[] SlotsForProbe=>_buttons.Select(pair=>(pair.Key,pair.Value.Button,pair.Value.Image)).ToArray();
    internal object InspectLayout()
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        return new {
            Count=_buttons.Count,IconSizeDip=IconSize,TargetSizeDip=TargetSize,
            DpiScaleX=dpi.DpiScaleX,DpiScaleY=dpi.DpiScaleY,
            ContentWidth=ActualWidth,ContentHeight=ActualHeight,Columns=Math.Max(1,(int)(_items.ActualWidth/TargetSize)),
            Icons=_buttons.Values.Select(entry=>new {
                WidthDip=entry.Image.ActualWidth,HeightDip=entry.Image.ActualHeight,
                WidthPhysical=entry.Image.ActualWidth*dpi.DpiScaleX,
                HeightPhysical=entry.Image.ActualHeight*dpi.DpiScaleY
            }).ToArray()
        };
    }
    private void Invoke(string key,TrayClick action,FrameworkElement element)
    {
        if(_disposed || !TryPlace(key,element,out var point)) return;
        if(action==TrayClick.Secondary && _host?.Snapshot.FirstOrDefault(icon=>icon.Key==key) is { } owner)
            Program.TrayMenus?.Begin(owner.WindowHandle);
        if(_host?.Invoke(key,action,point)!=true && !_disposed) { Program.TrayMenus?.Cancel(); _status("应用已退出或暂不可用"); }
    }
    private void Place(string key,FrameworkElement element)=>TryPlace(key,element,out _);
    private bool TryPlace(string key,FrameworkElement element,out Point anchor)
    {
        anchor=default;
        if(_disposed || !element.IsLoaded || PresentationSource.FromVisual(element) is null) return false;
        var bounds=new Rect(element.PointToScreen(new Point()),element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight)));
        anchor=new Point(bounds.Left+bounds.Width/2,bounds.Top+bounds.Height/2);
        _anchors[key]=anchor;
        _host?.UpdatePlacement(key,bounds);
        return true;
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true; _refresh.Stop();
        if(_host is not null) _host.Changed-=OnChanged;
        foreach(var pair in _anchors) _host?.Hover(pair.Key,false,pair.Value);
        _anchors.Clear();
    }
}
