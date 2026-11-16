using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed class DesktopBoxesSettingsWindow : GlassWindow
{
    private readonly DesktopBoxManager _manager;
    private readonly StackPanel _rows=new();
    private readonly List<GlassCard> _cards=[];
    private readonly TextBlock _status=Ui.Text("",11,Ui.Muted);
    private readonly Button _enabled,_addDesktop;
    private double _preferredCoreHeight=420;
    private bool _rebuilding,_rebuildQueued,_closed;
    protected override bool NoActivate=>false;

    internal DesktopBoxesSettingsWindow(DesktopBoxManager manager):base("uilicd · 桌面收纳")
    {
        _manager=manager; ShowActivated=true; Width=452; Height=_preferredCoreHeight+12; WindowStartupLocation=WindowStartupLocation.CenterScreen;
        var layout=new Grid { Margin=new Thickness(20,17,20,16) };
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
        var header=new DockPanel { Margin=new Thickness(0,0,0,12) };
        _enabled=Ui.Button(Ui.Text("",12),"显示 / 隐藏收纳盒",()=> { _manager.Configuration.Enabled=!_manager.Configuration.Enabled; _manager.Update(refresh:true); },72,30);
        DockPanel.SetDock(_enabled,Dock.Right); header.Children.Add(_enabled);
        var title=Ui.Text("桌面收纳",20); title.FontWeight=FontWeights.SemiBold; title.VerticalAlignment=VerticalAlignment.Center; header.Children.Add(title); layout.Children.Add(header);
        var scroll=new ScrollViewer { Content=_rows,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,PanningMode=PanningMode.VerticalOnly };
        Grid.SetRow(scroll,1); layout.Children.Add(scroll);
        var footer=new DockPanel { Margin=new Thickness(0,9,0,0) };
        var done=Ui.Button(Ui.Text("完成",12),"完成",Close,58,30); DockPanel.SetDock(done,Dock.Right); footer.Children.Add(done);
        var add=Ui.Button(Ui.Text("＋ 新建",12),"新建映射盒",_manager.PromptCreateBox,84,30); footer.Children.Add(add);
        _addDesktop=Ui.Button(Ui.Text("添加桌面项目",12),"添加桌面项目",AddDesktopItems,124,30); footer.Children.Add(_addDesktop);
        _status.VerticalAlignment=VerticalAlignment.Center; footer.Children.Add(_status); Grid.SetRow(footer,2); layout.Children.Add(footer);
        ContentLayer.Children.Add(layout); _manager.Changed+=ManagerChanged;
        Loaded+=(_,_)=> { Reposition(); Rebuild(); };
        Closed+=(_,_)=> { _closed=true; _manager.Changed-=ManagerChanged; };
    }

    private void ManagerChanged(object? sender,EventArgs e)
    {
        if(_closed || _rebuildQueued) return; _rebuildQueued=true;
        Dispatcher.BeginInvoke(new Action(()=> { _rebuildQueued=false; if(!_closed) Rebuild(); }),System.Windows.Threading.DispatcherPriority.Background);
    }
    private async void AddDesktopItems()
    {
        _addDesktop.IsEnabled=false;
        try
        {
            int count=await _manager.AddDesktopItemsAsync();
            _status.Text=_manager.LastError is not null?"部分未添加":count>0?"＋"+count+" 项":"已是最新";
            _status.ToolTip=_manager.LastError;
        }
        catch(Exception exception) { _status.Text="未能添加"; _status.ToolTip=exception.Message; }
        finally { _addDesktop.IsEnabled=true; }
    }
    private void Rebuild()
    {
        if(_rebuilding || _closed) return; _rebuilding=true;
        try
        {
        foreach(var card in _cards) card.Dispose(); _cards.Clear(); _rows.Children.Clear();
        if(_enabled.Content is TextBlock caption) caption.Text=_manager.Configuration.Enabled?"已显示":"已隐藏";
        foreach(var box in _manager.Configuration.Boxes)
        {
            var content=new StackPanel { Margin=new Thickness(12,9,12,9) };
            var row=new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(55) });
            var name=new TextBox { Text=box.Name,FontSize=14,FontWeight=FontWeights.SemiBold,Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0,4,0,4),MaxLength=40,Foreground=Ui.Ink,VerticalContentAlignment=VerticalAlignment.Center };
            name.LostKeyboardFocus+=(_,_)=> { if(_rebuilding || _closed) return; string value=name.Text.Trim(); if(value.Length>0 && value!=box.Name) { box.Name=value; _manager.Update(box); } };
            name.PreviewKeyDown+=(_,e)=> { if(e.Key==System.Windows.Input.Key.Enter) { e.Handled=true; KeyboardMoveFocus(name); } };
            row.Children.Add(name);
            var remove=Ui.Button(Ui.Text("移除",11),"移除收纳盒",()=>_manager.RemoveBox(box),48,28); Grid.SetColumn(remove,1); row.Children.Add(remove); content.Children.Add(row);
            var actions=new WrapPanel();
            actions.Children.Add(Ui.Button(Ui.Text(box.Collapsed?"展开":"收起",11),"展开 / 收起此盒",()=> { box.Collapsed=!box.Collapsed; _manager.Update(box); },55,28));
            actions.Children.Add(Ui.Button(Ui.Text(box.Enabled?"显示":"隐藏",11),"显示 / 隐藏此盒",()=> { box.Enabled=!box.Enabled; _manager.Update(refresh:true); },55,28));
            actions.Children.Add(Ui.Button(Ui.Text(box.AutoSize?"自适应":"手动",11),"尺寸模式",()=> { box.AutoSize=!box.AutoSize; _manager.Update(box); },62,28));
            var count=Ui.Text(box.Items.Count+" 项",11,Ui.Muted); count.VerticalAlignment=VerticalAlignment.Center; count.Margin=new Thickness(8,0,0,0); actions.Children.Add(count); content.Children.Add(actions);
            var card=new GlassCard { CornerRadius=16,Tint=.055f,Content=content,Margin=new Thickness(0,0,0,8) };
            card.LuminanceChanged+=luminance=>SetInk(content,luminance<.48?Brushes.White:Ui.Ink);
            _cards.Add(card); _rows.Children.Add(card);
        }
        _status.Text=_manager.LastError is not null?"暂不可用":"";
        _status.ToolTip=_manager.LastError;
        _preferredCoreHeight=Math.Clamp(145+_manager.Configuration.Boxes.Count*98,260,650); Reposition();
        }
        finally { _rebuilding=false; }
    }
    private static void KeyboardMoveFocus(TextBox text)=>text.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
    private static void SetInk(DependencyObject node,Brush ink)
    {
        if(node is TextBlock text) text.Foreground=ink;
        if(node is TextBox entry) entry.Foreground=ink;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) SetInk(VisualTreeHelper.GetChild(node,i),ink);
    }
    protected override void OnMaterialLuminance(double luminance)=>SetInk(ContentLayer,luminance<.48?Brushes.White:Ui.Ink);
    protected override void Reposition()
    {
        if(!IsLoaded) return;
        var bounds=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
        Height=Math.Min(_preferredCoreHeight,(bounds.Bottom-bounds.Top)/dpi.DpiScaleY-24)+12;
        Left=Math.Clamp(Left,bounds.Left/dpi.DpiScaleX+2,Math.Max(bounds.Left/dpi.DpiScaleX+2,bounds.Right/dpi.DpiScaleX-Width-2));
        Top=Math.Clamp(Top,bounds.Top/dpi.DpiScaleY+2,Math.Max(bounds.Top/dpi.DpiScaleY+2,bounds.Bottom/dpi.DpiScaleY-Height-2));
        SetSurface(new Rect(8,8,Width-16,Height-16),23); RenderGlass();
    }
}
