using System.Diagnostics;
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

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxWindow : GlassWindow
{
    private const double IconSize=32,TileWidth=DesktopBoxLayout.ColumnStep,RowHeight=DesktopBoxLayout.RowStep,ChromeHeight=46,ShadowFrame=6;
    private readonly DesktopBoxManager _manager;
    private readonly DesktopBoxDefinition _box;
    private readonly DesktopBoxMagnetism _magnetism;
    private readonly TextBlock _title=Ui.Text("",13),_status=Ui.Text("",11,Ui.Muted);
    private readonly ListBox _items=new() { Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch,Focusable=false };
    private readonly Grid _body=new() { Visibility=Visibility.Collapsed };
    private readonly TextBlock _empty=Ui.Text("拖入文件",12,Ui.Muted);
    private readonly Button _collapse;
    private readonly TextBlock _collapseGlyph=Ui.Glyph("\uE70D",13);
    private readonly DispatcherTimer _hideTimer=new() { Interval=TimeSpan.FromMilliseconds(500) };
    private bool _expanded,_hover,_closed;
    private int _openMenus;
    private nint _lastForeground;
    private bool? _dark;
    private int _rowColumns,_iconPixels,_rowFingerprint;
    private bool _groupShown=true;
    private bool _desktopSuspended;
    internal bool KeepsGroupVisible=>_moving || _resizing || _itemDragging || _dropPreview is not null || _openMenus>0;
    internal bool DesktopPresentationVisible=>SurfaceVisible;
    internal bool ExpectedVisible=>_groupShown && !_desktopSuspended && !_closed;
    internal int ColumnCount=>Math.Max(2,(int)(((_previewSize?.Width??_box.Width)-24)/TileWidth));
    internal string BoxId=>_box.Id;
    internal ContextMenu BoxContextMenu=>ContentLayer.ContextMenu;

    internal DesktopBoxWindow(DesktopBoxManager manager,DesktopBoxDefinition box):base("Veyra · "+box.Name)
    {
        _manager=manager; _box=box; Topmost=false;
        _magnetism=new DesktopBoxMagnetism(_items,()=>_expanded && SurfaceVisible && !_closed && !_desktopSuspended && _groupShown && !_moving && !_resizing && !_itemDragging && _dropPreview is null && _openMenus==0);
        _items.PreviewMouseMove+=(_,e)=> {if(e.StylusDevice is null && e.LeftButton!=MouseButtonState.Pressed)_magnetism.PointAt(e.GetPosition(_items));};
        _items.MouseLeave+=(_,_)=>_magnetism.Leave();
        _items.PreviewMouseDown+=(_,_)=>_magnetism.Reset();
        _items.PreviewMouseWheel+=(_,_)=>_magnetism.Reset();
        var layout=new Grid { Margin=new Thickness(10,5,10,8) };
        layout.RowDefinitions.Add(new RowDefinition { Height=new GridLength(29) });
        layout.RowDefinitions.Add(new RowDefinition());
        var header=new Grid { Background=Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(27) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(27) });
        _title.FontWeight=FontWeights.SemiBold; _title.VerticalAlignment=VerticalAlignment.Center; _title.TextTrimming=TextTrimming.CharacterEllipsis;
        _title.TextWrapping=TextWrapping.NoWrap; _title.ToolTip="拖动移动收纳盒"; header.Children.Add(_title);
        _collapse=Ui.Button(_collapseGlyph,"展开 / 收起",()=>_manager.SetCollapsed(_box,_expanded),25,25);
        var create=Ui.Button(Ui.Glyph("\uE710",14),"新建映射盒",_manager.PromptCreateBox,25,25);
        Grid.SetColumn(create,1); Grid.SetColumn(_collapse,2);
        header.Children.Add(create); header.Children.Add(_collapse); layout.Children.Add(header);
        var move=new Thumb { Cursor=Cursors.SizeAll,Background=Brushes.Transparent };
        var moveTemplate=new ControlTemplate(typeof(Thumb)); var moveSurface=new FrameworkElementFactory(typeof(Border));
        moveSurface.SetValue(Border.BackgroundProperty,Brushes.Transparent); moveTemplate.VisualTree=moveSurface; move.Template=moveTemplate;
        header.Children.Add(move); move.DragStarted+=BeginMove; move.DragDelta+=MoveBox; move.DragCompleted+=FinishMove;
        move.MouseDoubleClick+=(_,e)=> { _manager.SetCollapsed(_box,_expanded); e.Handled=true; };
        var boxMenu=new ContextMenu(); boxMenu.Items.Add(Ui.Menu("重命名…",()=>_manager.PromptRename(_box)));
        boxMenu.Items.Add(Ui.Menu("新建映射盒…",_manager.PromptCreateBox));
        boxMenu.Items.Add(Ui.Menu("自适应大小",()=> { _box.AutoSize=!_box.AutoSize; _manager.Update(_box); },true,_box.AutoSize));
        boxMenu.Items.Add(Ui.Menu("管理收纳盒…",_manager.ShowSettings));
        boxMenu.Items.Add(Ui.Menu("添加回收站",()=>_= _manager.AddReferencesAsync(_box,new[]{DesktopShellReference.RecycleBin})));
        boxMenu.Items.Add(new Separator()); boxMenu.Items.Add(Ui.Menu("移除收纳盒",()=>_manager.RemoveBox(_box)));
        boxMenu.Opened+=(_,_)=> { if(boxMenu.Items[2] is MenuItem adaptive) adaptive.IsChecked=_box.AutoSize; };
        TrackMenu(boxMenu); ContentLayer.ContextMenu=boxMenu;

        ScrollViewer.SetVerticalScrollBarVisibility(_items,ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(_items,ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(_items,true); ScrollViewer.SetPanningMode(_items,PanningMode.VerticalOnly);
        VirtualizingPanel.SetIsVirtualizing(_items,true); VirtualizingPanel.SetVirtualizationMode(_items,VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(_items,ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(_items,new VirtualizationCacheLength(.5));
        VirtualizingPanel.SetCacheLengthUnit(_items,VirtualizationCacheLengthUnit.Page);
        _items.ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        _items.UseLayoutRounding=true; _items.SnapsToDevicePixels=true;
        var template=new DataTemplate { VisualTree=new FrameworkElementFactory(typeof(DesktopBoxItemRow)) }; _items.ItemTemplate=template;
        var container=new Style(typeof(ListBoxItem));
        container.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
        container.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
        container.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
        container.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        container.Setters.Add(new Setter(UIElement.FocusableProperty,false));
        var itemTemplate=new ControlTemplate(typeof(ListBoxItem)); var content=new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty,new TemplateBindingExtension(ContentControl.ContentProperty));
        content.SetValue(ContentPresenter.ContentTemplateProperty,new TemplateBindingExtension(ContentControl.ContentTemplateProperty));
        itemTemplate.VisualTree=content; container.Setters.Add(new Setter(Control.TemplateProperty,itemTemplate)); _items.ItemContainerStyle=container;
        _body.Children.Add(_items); _empty.HorizontalAlignment=HorizontalAlignment.Center; _empty.VerticalAlignment=VerticalAlignment.Center; _body.Children.Add(_empty);
        _status.HorizontalAlignment=HorizontalAlignment.Left; _status.VerticalAlignment=VerticalAlignment.Bottom; _body.Children.Add(_status);
        Grid.SetRow(_body,1); layout.Children.Add(_body); ContentLayer.Children.Add(layout);
        foreach(var corner in new[]{new Vector(-1,-1),new Vector(-1,1)})
        {
            var resize=new Thumb { Width=14,Height=14,Tag=corner,Background=Brushes.Transparent,Cursor=corner.X==corner.Y?Cursors.SizeNWSE:Cursors.SizeNESW,HorizontalAlignment=corner.X<0?HorizontalAlignment.Left:HorizontalAlignment.Right,VerticalAlignment=corner.Y<0?VerticalAlignment.Top:VerticalAlignment.Bottom,ToolTip="拖动调整大小" };
            var resizeTemplate=new ControlTemplate(typeof(Thumb)); var resizeSurface=new FrameworkElementFactory(typeof(Border));
            resizeSurface.SetValue(Border.BackgroundProperty,Brushes.Transparent); resizeTemplate.VisualTree=resizeSurface; resize.Template=resizeTemplate;
            resize.DragStarted+=BeginResize; resize.DragDelta+=ResizeBox; resize.DragCompleted+=FinishResize;
            Grid.SetRowSpan(resize,2); layout.Children.Add(resize);
        }
        var edges=new Grid { Margin=new Thickness(0,43,0,0) };
        edges.SetBinding(VisibilityProperty,new Binding(nameof(Visibility)) { Source=_body });
        foreach(var direction in new[]{new Vector(-1,0),new Vector(1,0),new Vector(0,1)})
        {
            bool bottom=direction.Y!=0;
            var resize=new Thumb {
                Tag=direction,Background=Brushes.Transparent,Cursor=bottom?Cursors.SizeNS:Cursors.SizeWE,
                Width=bottom?double.NaN:6,Height=bottom?6:double.NaN,
                HorizontalAlignment=bottom?HorizontalAlignment.Stretch:direction.X<0?HorizontalAlignment.Left:HorizontalAlignment.Right,
                VerticalAlignment=bottom?VerticalAlignment.Bottom:VerticalAlignment.Stretch,
                Margin=bottom?new Thickness(14,0,14,0):new Thickness(0,0,0,14),ToolTip=bottom?"拖动调整高度":"拖动调整宽度"
            };
            var edgeTemplate=new ControlTemplate(typeof(Thumb)); var border=new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty,Brushes.Transparent); edgeTemplate.VisualTree=border; resize.Template=edgeTemplate;
            resize.DragStarted+=BeginResize; resize.DragDelta+=ResizeBox; resize.DragCompleted+=FinishResize; edges.Children.Add(resize);
        }
        ContentLayer.Children.Add(edges);
        ContentLayer.Background=Brushes.Transparent; ContentLayer.AllowDrop=true;
        InitializeItemDragging();
        ContentLayer.MouseEnter+=(_,_)=> { _hover=true; _hideTimer.Stop(); _manager.RevealGroup(); if(!_box.Collapsed) SetExpanded(true); };
        ContentLayer.MouseLeave+=(_,_)=> { _hover=false; };
        _hideTimer.Tick+=(_,_)=>_hideTimer.Stop();
        Loaded+=(_,_)=> { RefreshModel(); UpdateDesktopLayer(); };
        Closed+=(_,_)=> { _closed=true; _manager.EndDrag(); _hideTimer.Stop(); DisposeItemDragging(); };
    }

    internal void RefreshModel()
    {
        _title.Text=_box.Name; Title="Veyra · "+_box.Name;
        var currentIds=_box.Items.Select(item=>item.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var id in _itemViews.Keys.Where(id=>!currentIds.Contains(id)).ToArray()) { _magnetism.Remove(_itemViews[id]); _itemMotion.Remove(_itemViews[id]); _itemViews.Remove(id); }
        UpdateRows();
        _empty.Visibility=_box.Items.Count==0?Visibility.Visible:Visibility.Hidden;
        SetExpanded(!_box.Collapsed); Reposition();
    }
    internal void RefreshSourceReferences()
    {
        var positions=CaptureItemPositions(); RefreshModel(); AnimateItemReflow(positions);
    }

    private void TrackMenu(ContextMenu menu)
    {
        menu.Opened+=(_,_)=> { _openMenus++; _hideTimer.Stop(); };
        menu.Closed+=(_,_)=> { _openMenus=Math.Max(0,_openMenus-1); if(!_hover && !_box.Pinned) _hideTimer.Start(); };
    }
    private static DragDropEffects ReferenceEffect(DragDropEffects allowed)=>(allowed&DragDropEffects.Link)!=0?DragDropEffects.Link:(allowed&DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;

    private void SetExpanded(bool expanded)
    {
        if(_closed || _expanded==expanded) return;
        _expanded=expanded; _body.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
        if(!expanded)_magnetism.Reset();
        _collapseGlyph.RenderTransformOrigin=new Point(.5,.5); _collapseGlyph.RenderTransform=new RotateTransform(expanded?180:0);
        Reposition();
    }
    internal void RevealForCapture() { _hideTimer.Stop(); SetExpanded(true); }
    internal void SetDesktopSuspended(bool suspended)
    {
        if(_desktopSuspended==suspended || _closed)return;
        _desktopSuspended=suspended;
        if(suspended)
        {
            ClearItemDragPreview(); _hideTimer.Stop(); Hide(); SurfaceVisible=false; ReleaseMaterial(true);
            _items.ItemsSource=null; _rowFingerprint=int.MinValue; ReleaseItemViews();
        }
        else if(_groupShown)
        {
            if(!IsVisible)Show(); RefreshModel(); UpdateDesktopLayer();
        }
    }
    internal void SetGroupShown(bool shown)
    {
        bool visible=shown && !_desktopSuspended;
        if(_closed || (_groupShown==shown && IsVisible==visible)) return; _groupShown=shown;
        if(visible) { if(!IsVisible)Show(); Reposition(); UpdateDesktopLayer(); }
        else { ClearItemDragPreview(); Hide(); ReleaseMaterial(true); _items.ItemsSource=null; _rowFingerprint=int.MinValue; ReleaseItemViews(); }
    }

    private void UpdateRows(bool force=false)
    {
        if(_desktopSuspended || !_groupShown) { _items.ItemsSource=null; _rowFingerprint=int.MinValue; return; }
        int columns=ColumnCount;
        var dpi=VisualTreeHelper.GetDpi(this);
        int iconPixels=Math.Clamp((int)Math.Ceiling(IconSize*Math.Min(1.5,1+Program.Prefs.Magnification)*Math.Max(dpi.DpiScaleX,dpi.DpiScaleY)),32,256);
        IEnumerable<DesktopBoxItem?> display=_dropPreview??_box.Items.Cast<DesktopBoxItem?>();
        var hash=new HashCode(); foreach(var item in display)hash.Add(item);
        int fingerprint=hash.ToHashCode();
        if(!force && columns==_rowColumns && iconPixels==_iconPixels && fingerprint==_rowFingerprint) return;
        _rowColumns=columns; _iconPixels=iconPixels; _rowFingerprint=fingerprint;
        // Only row references are grouped here. Native icons are resolved by the
        // recycling row containers, so a 500-item box does not realize 500 tiles.
        _items.ItemsSource=display.Chunk(columns).Select(items=>items.ToArray()).ToArray();
    }

    protected override void Reposition()
    {
        if(!IsLoaded || _closed) return;
        if(_previewSize is null && _box.AutoSize && !_resizing)
        {
            var bounds=DesktopNative.MonitorRect(this); var currentDpi=VisualTreeHelper.GetDpi(this);
            var adaptive=DesktopBoxLayout.AdaptiveSize(_box.Items.Count,new Size((bounds.Right-bounds.Left)/currentDpi.DpiScaleX-32,(bounds.Bottom-bounds.Top)/currentDpi.DpiScaleY-140));
            _box.Width=adaptive.Width; _box.Height=adaptive.Height;
        }
        else if(_previewSize is null && !_box.AutoSize && !_resizing)
        {
            var bounds=DesktopNative.MonitorRect(this); var currentDpi=VisualTreeHelper.GetDpi(this);
            var snapped=DesktopBoxLayout.ResizeSize(new Size(_box.Width,_box.Height),new Size((bounds.Right-bounds.Left)/currentDpi.DpiScaleX-12,(bounds.Bottom-bounds.Top)/currentDpi.DpiScaleY-12));
            _box.Width=snapped.Width; _box.Height=snapped.Height;
        }
        UpdateRows();
        double coreWidth=_previewSize?.Width??_box.Width;
        double contentHeight=ChromeHeight+Math.Max(1,Math.Ceiling((double)(_dropPreview?.Count??_box.Items.Count)/ColumnCount))*RowHeight;
        double coreHeight=_expanded?(_box.AutoSize?Math.Min(_previewSize?.Height??_box.Height,Math.Max(112,contentHeight)):_previewSize?.Height??_box.Height):44;
        Width=coreWidth+2*ShadowFrame; Height=coreHeight+2*ShadowFrame;
        var monitor=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
        double left=monitor.Left/dpi.DpiScaleX,top=monitor.Top/dpi.DpiScaleY;
        double right=monitor.Right/dpi.DpiScaleX,bottom=monitor.Bottom/dpi.DpiScaleY;
        double placedLeft=Math.Clamp(_box.Left,left+6,Math.Max(left+6,right-coreWidth-6));
        double placedTop=Math.Clamp(_box.Top,top+6,Math.Max(top+6,bottom-coreHeight-6));
        if(_dropPreview is null) { _box.Left=placedLeft; _box.Top=placedTop; }
        Left=placedLeft-ShadowFrame; Top=placedTop-ShadowFrame;
        SetSurface(new Rect(8,8,Width-16,Height-16),17); RenderGlass();
    }

    internal void UpdateDesktopLayer()
    {
        if(_closed || _desktopSuspended || !_groupShown || !IsVisible || _moving || _resizing || _itemDragging || _dropPreview is not null) return;
        var foreground=DesktopNative.GetForegroundWindow();
        if(foreground!=_lastForeground || !DesktopNative.IsAtDesktopLayer(this))
        { _lastForeground=foreground; DesktopNative.PlaceFloatingDesktopSurface(this); }
        bool visible=Program.CapturePreview || !DesktopNative.ForegroundCoversMonitor(this);
        if(visible)_=RefreshShellIcons();
        if(SurfaceVisible==visible) return;
        SurfaceVisible=visible; if(visible) RenderGlass(); else {_magnetism.Reset(); ReleaseMaterial();}
    }

    protected override void OnMaterialLuminance(double luminance)
    {
        bool dark=luminance<.48; if(_dark==dark) return; _dark=dark;
        SetInk(ContentLayer,dark?Brushes.White:Ui.Ink);
    }
    private static void SetInk(DependencyObject node,Brush ink)
    {
        if(node is TextBlock text) text.Foreground=ink;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) SetInk(VisualTreeHelper.GetChild(node,i),ink);
    }

    internal FrameworkElement BuildItem(DesktopBoxItem item)
    {
        var image=new Image { Width=IconSize,Height=IconSize,Stretch=Stretch.Uniform,Margin=new Thickness(0,1,0,3),UseLayoutRounding=true,SnapsToDevicePixels=true };
        RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        var name=Ui.Text(item.Name,10.5,_dark==true?Brushes.White:Ui.Ink);
        name.TextAlignment=TextAlignment.Center; name.TextTrimming=TextTrimming.CharacterEllipsis;
        name.TextWrapping=TextWrapping.NoWrap; name.Height=15; name.MaxWidth=TileWidth-12;
        var contents=new StackPanel { VerticalAlignment=VerticalAlignment.Center };
        contents.Children.Add(image); contents.Children.Add(name);
        var frame=new Border {
            Width=TileWidth-4,Height=RowHeight-4,CornerRadius=new CornerRadius(11),
            BorderThickness=new Thickness(.7),
            BorderBrush=new LinearGradientBrush(Color.FromArgb(38,255,255,255),Color.FromArgb(10,225,235,246),90),
            Background=new LinearGradientBrush(Color.FromArgb(9,255,255,255),Color.FromArgb(2,230,240,250),90),
            Child=contents
        };
        var button=Ui.Button(frame,item.Name,()=> { },double.NaN,RowHeight);
        button.Focusable=true; button.ToolTip=item.Name+"\n"+item.Path;
        button.UseLayoutRounding=true; button.SnapsToDevicePixels=true;
        button.MouseDoubleClick+=(_,e)=> { if(e.ChangedButton==MouseButton.Left) { OpenItem(item); e.Handled=true; } };
        button.PreviewKeyDown+=(_,e)=> { if(e.Key==Key.Enter) { OpenItem(item); e.Handled=true; } };
        var menu=new ContextMenu(); menu.Items.Add(Ui.Menu("打开",()=>OpenItem(item)));
        menu.Items.Add(Ui.Menu("复制路径",()=>Clipboard.SetText(item.Path)));
        menu.Items.Add(Ui.Menu("打开所在位置",()=> {
            if(DesktopShellReference.IsRecycleBin(item.Path)) { OpenItem(item); return; }
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")) { Arguments="/select,\""+item.Path+"\"",UseShellExecute=true }); }
            catch(Exception exception) { ShowFailure("无法定位",exception); }
        }));
        menu.Items.Add(Ui.Menu("从收纳盒移除",()=>_manager.RemoveReference(_box,item))); TrackMenu(menu); button.ContextMenu=menu;
        Point pressed=default; bool pressedHere=false;
        button.PreviewMouseLeftButtonDown+=(_,e)=> { pressed=e.GetPosition(button); pressedHere=true; };
        button.PreviewMouseLeftButtonUp+=(_,_)=>pressedHere=false;
        button.LostMouseCapture+=(_,_)=> { if(!_itemDragging)pressedHere=false; };
        button.PreviewMouseMove+=(_,e)=> {
            if(!pressedHere || _itemDragging || e.LeftButton!=MouseButtonState.Pressed || (e.GetPosition(button)-pressed).Length<6) return;
            var data=DesktopShellReference.CreateDragData(item); data.SetData("GlassDock.BoxReference",_box.Id+":"+item.Id);
            _itemDragging=true; _hideTimer.Stop();
            try { DragDrop.DoDragDrop(button,data,DragDropEffects.Copy|DragDropEffects.Link); }
            finally { pressedHere=false; _itemDragging=false; _manager.ClearItemDragPreviews(); UpdateDesktopLayer(); if(!_box.Pinned && !_hover) _hideTimer.Start(); }
            e.Handled=true;
        };
        _magnetism.Register(button,image,name);
        _=LoadIcon(image,button,item,_iconPixels); return button;
    }

    private static async Task LoadIcon(Image image,Button button,DesktopBoxItem item,int pixels)
    {
        try
        {
            // Shortcut resolution and shell icon extraction share the existing STA
            // worker, with pixels matched to the current monitor's physical size.
            var native=await IconService.GetFileIconAsync(item.Path,pixels);
            image.Source=native.Image;
        }
        catch(Exception exception) { button.Opacity=.5; button.ToolTip=exception.Message; }
    }

    private void OpenItem(DesktopBoxItem item)
    {
        try { Process.Start(DesktopShellReference.OpenInfo(item.Path)); }
        catch(Exception exception) { ShowFailure("无法打开",exception); }
    }
    private void ShowFailure(string message,Exception exception) { _status.Text=message; _status.ToolTip=exception.Message; }

    internal object InspectLayout()
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        var rows=Descendants<DesktopBoxItemRow>(_items).ToArray();
        return new {
            _box.Id,_box.Name,_box.Kind,_box.AutoSize,Expanded=_expanded,_box.Pinned,Items=_box.Items.Count,Columns=ColumnCount,
            Moving=_moving,Resizing=_resizing,DesktopSuspended=_desktopSuspended,DragPreview=_dropPreview is not null,InsertionIndex=_dropIndex,ReservedSlots=_dropSlots,AnimatingTiles=_itemMotion.Count,
            Magnetism=_magnetism.Inspect(),MagnetismAnimating=_magnetism.Animating,
            Rows=_items.Items.Count,RealizedRows=rows.Length,RealizedTiles=rows.Sum(row=>row.Children.Count),
            IconSizeDip=IconSize,IconPixels=_iconPixels,TileWidthDip=TileWidth,RowHeightDip=RowHeight,
            DpiScaleX=dpi.DpiScaleX,DpiScaleY=dpi.DpiScaleY,
            WindowDip=new { X=Left,Y=Top,Width,Height },
            StoredPlacement=new { _box.Left,_box.Top,_box.Width,_box.Height },
            WindowPhysical=ScreenBounds(this),ContentPhysical=ScreenBounds(_items),
            MaterialPhysical=ScreenBounds(Glass),
            VerticalScrollBar=ScrollViewer.GetVerticalScrollBarVisibility(_items).ToString(),
            HorizontalScrollBar=ScrollViewer.GetHorizontalScrollBarVisibility(_items).ToString(),
            Virtualizing=VirtualizingPanel.GetIsVirtualizing(_items)
        };
    }
    private static object ScreenBounds(FrameworkElement element)
    {
        var top=element.PointToScreen(new Point());
        var bottom=element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight));
        return new { X=top.X,Y=top.Y,Width=bottom.X-top.X,Height=bottom.Y-top.Y };
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject node) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
        {
            var child=VisualTreeHelper.GetChild(node,i);
            if(child is T item) yield return item;
            foreach(var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}

/// <summary>Adaptive icon rows are realized only near the visible recycling viewport.</summary>
public sealed class DesktopBoxItemRow : UniformGrid
{
    public DesktopBoxItemRow()
    {
        Rows=1; Loaded+=(_,_)=>Build(); DataContextChanged+=(_,_)=> { if(IsLoaded) Build(); };
    }
    private void Build()
    {
        if(Window.GetWindow(this) is not DesktopBoxWindow window || DataContext is not DesktopBoxItem?[] items) return;
        Columns=window.ColumnCount; Children.Clear();
        foreach(var item in items) Children.Add(window.ItemView(item));
    }
}
