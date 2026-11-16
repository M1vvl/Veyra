using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using GlassDock.Services;

namespace GlassDock;

internal sealed partial class SettingsWindow : GlassWindow
{
    protected override bool NoActivate=>false;
    private readonly StackPanel _sections=new() {Margin=new Thickness(20,0,20,10)};
    private static readonly DropShadowEffect LightTextShadow=TextShadow(Colors.Black);
    private static readonly DropShadowEffect DarkTextShadow=TextShadow(Colors.White);
    private readonly byte[] _contrastPixel=new byte[4];
    internal SettingsWindow():base("uilicd · 设置")
    {
        Width=500; Height=Math.Min(760,SystemParameters.WorkArea.Height-32); MinWidth=450; MinHeight=540; Topmost=false; ShowActivated=true;
        ResizeMode=ResizeMode.CanResizeWithGrip; WindowStartupLocation=WindowStartupLocation.CenterScreen;
        FontFamily=new FontFamily("Segoe UI Variable, Microsoft YaHei UI");
        var layout=new Grid {Margin=new Thickness(6)};
        layout.RowDefinitions.Add(new RowDefinition {Height=new GridLength(74)});
        layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition {Height=new GridLength(52)});
        var header=new Grid {Margin=new Thickness(20,10,12,2)};
        header.ColumnDefinitions.Add(new ColumnDefinition());
        for(int i=0;i<3;i++)header.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(30)});
        var title=Ui.Text("uilicd",25); title.FontWeight=FontWeights.SemiBold; title.VerticalAlignment=VerticalAlignment.Center; header.Children.Add(title);
        var drag=new Thumb {Background=Brushes.Transparent,Cursor=Cursors.SizeAll};
        var dragTemplate=new ControlTemplate(typeof(Thumb)); var dragFace=new FrameworkElementFactory(typeof(Border)); dragFace.SetValue(Border.BackgroundProperty,Brushes.Transparent); dragTemplate.VisualTree=dragFace; drag.Template=dragTemplate;
        drag.DragDelta+=(_,e)=> {if(WindowState==WindowState.Normal) {Left+=e.HorizontalChange; Top+=e.VerticalChange;}};
        drag.MouseDoubleClick+=(_,_)=>ToggleMaximize(); header.Children.Add(drag);
        AddHeaderButton(header,1,"\uE921","最小化设置",()=>WindowState=WindowState.Minimized);
        AddHeaderButton(header,2,"\uE922","最大化 / 还原设置",ToggleMaximize);
        AddHeaderButton(header,3,"\uE8BB","关闭设置",Close); layout.Children.Add(header);
        var scroll=new ScrollViewer {Content=_sections,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,PanningMode=PanningMode.VerticalOnly}; Grid.SetRow(scroll,1); layout.Children.Add(scroll);
        var startup=StartupService.Read(); var general=Section("常规");
        AddSwitch(general,"开机启动",startup.Enabled,value=>StartupService.SetEnabled(value).Enabled,startup.Error);
        AddSwitch(general,"替代 Windows 任务栏",Program.Prefs.ReplaceTaskbar,value=> {Program.Prefs.ReplaceTaskbar=value; Program.ApplyPreferences(); return value;});
        AddSidebarMode(general);
        AddSwitch(general,"显示灵动岛屿",Program.Prefs.ShowIsland,value=> {Program.Prefs.ShowIsland=value; Program.ApplyPreferences(); return value;});
        var material=Section("材质");
        AddSwitch(material,"Liquid Glass",Program.Prefs.GlassEnabled,value=> {Program.Prefs.GlassEnabled=value; Program.ApplyPreferences(); return value;});
        var defaults=new Preferences();
        AddSlider(material,"玻璃层透明度",0,1,Program.Prefs.GlassTransparency,defaults.GlassTransparency,value=>Program.Prefs.GlassTransparency=value,value=>$"{value:P0}");
        AddFrameRate(material,defaults.GlassFramesPerSecond);
        var islandColors=Section("灵动岛配色");
        AddIslandColor(islandColors,"歌词颜色",()=>Program.Prefs.IslandLyricsColor,value=>Program.Prefs.IslandLyricsColor=value);
        AddIslandColor(islandColors,"进度条颜色",()=>Program.Prefs.IslandProgressColor,value=>Program.Prefs.IslandProgressColor=value);
        AddIslandColor(islandColors,"律动条颜色",()=>Program.Prefs.IslandMeterColor,value=>Program.Prefs.IslandMeterColor=value);
        var appearance=Section("外观与动效");
        AddSlider(appearance,"Dock 整体尺寸",.35,1,Program.Prefs.DockScale,defaults.DockScale,value=>Program.Prefs.DockScale=value,value=>$"{value:P0}");
        AddSlider(appearance,"图标大小",32,64,Program.Prefs.IconSize,defaults.IconSize,value=>Program.Prefs.IconSize=Math.Round(value),value=>$"{value:0}");
        AddSlider(appearance,"磁吸放大",0,.85,Program.Prefs.Magnification,defaults.Magnification,value=>Program.Prefs.Magnification=value,value=>$"{value:P0}");
        AddSwitch(appearance,"减少动态效果",Program.Prefs.ReducedMotion,value=> {Program.Prefs.ReducedMotion=value; Program.ApplyPreferences(); return value;});
        var actions=new Grid {Margin=new Thickness(22,4,22,8)};
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto}); actions.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var organizer=Ui.Button(Ui.Text("桌面收纳",12),"桌面收纳",()=>Program.DesktopBoxes?.ShowSettings(),92,30); organizer.HorizontalAlignment=HorizontalAlignment.Left; actions.Children.Add(organizer);
        var restore=Ui.Button(Ui.Text("恢复任务栏",12),"恢复 Windows 任务栏",()=> {Program.Prefs.ReplaceTaskbar=false; Program.ApplyPreferences();},100,30); Grid.SetColumn(restore,1); actions.Children.Add(restore);
        var quit=Ui.Button(Ui.Text("退出",12),"退出 uilicd",Program.Quit,52,30); Grid.SetColumn(quit,2); actions.Children.Add(quit);
        Grid.SetRow(actions,2); layout.Children.Add(actions); ContentLayer.Children.Add(layout);
        ContentLayer.Children.Add(new ResizeGrip {Width=14,Height=14,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Opacity=.45});
        Loaded+=(_,_)=>Reposition(); SizeChanged+=(_,_)=>Reposition();
        PreviewKeyDown+=(_,e)=> {if(e.Key==Key.Escape) {Close(); e.Handled=true;}};
    }
    private void ToggleMaximize()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    private static void AddHeaderButton(Grid header,int column,string glyph,string name,Action action)
    {
        var button=Ui.Button(Ui.Glyph(glyph,12),name,action,28,28,30); button.VerticalAlignment=VerticalAlignment.Center; Grid.SetColumn(button,column); header.Children.Add(button);
    }
    protected override void Reposition()
    {
        if(!IsLoaded)return;
        double width=ActualWidth>0?ActualWidth:Width,height=ActualHeight>0?ActualHeight:Height;
        SetSurface(new Rect(8,8,Math.Max(1,width-16),Math.Max(1,height-16)),26); RenderGlass();
    }
    protected override void OnMaterialLuminance(double luminance)
    {
        Brush Ink(FrameworkElement element)
        {
            // A window-wide average loses labels over mixed light/dark wallpaper.
            // Read the actual local background; never sample the final UI itself.
            if(Glass.Source is not BitmapSource frame || element.ActualWidth<1)return luminance<.48?Brushes.White:Ui.Ink;
            var origin=element.TranslatePoint(new Point(),Glass);
            Span<double> samples=stackalloc double[3];
            for(int i=0;i<3;i++)
            {
                int x=Math.Clamp((int)((origin.X+element.ActualWidth*(.2+i*.3))/Glass.ActualWidth*frame.PixelWidth),0,frame.PixelWidth-1);
                int y=Math.Clamp((int)((origin.Y+element.ActualHeight*.5)/Glass.ActualHeight*frame.PixelHeight),0,frame.PixelHeight-1);
                frame.CopyPixels(new Int32Rect(x,y,1,1),_contrastPixel,4,0);
                samples[i]=(.2126*_contrastPixel[2]+.7152*_contrastPixel[1]+.0722*_contrastPixel[0])/255;
            }
            samples.Sort(); return samples[1]<.57?Brushes.White:Ui.Ink;
        }
        void Walk(DependencyObject node)
        {
            if(node is TextBlock text) {text.Foreground=Ink(text); text.Effect=text.Foreground==Brushes.White?LightTextShadow:DarkTextShadow;}
            if(node is ComboBox control)control.Foreground=Ink(control);
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)Walk(VisualTreeHelper.GetChild(node,i));
        }
        Walk(ContentLayer);
    }
    private static DropShadowEffect TextShadow(Color color)
    {
        var shadow=new DropShadowEffect {Color=color,BlurRadius=2,ShadowDepth=0,Opacity=.55,RenderingBias=RenderingBias.Performance}; shadow.Freeze(); return shadow;
    }
}
