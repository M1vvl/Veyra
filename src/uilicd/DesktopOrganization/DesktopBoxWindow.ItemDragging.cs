using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using GlassDock.Docking;

namespace GlassDock.DesktopOrganization;

/// <summary>Transient insertion slots and viewport growth; source mappings change only on drop.</summary>
internal sealed partial class DesktopBoxWindow
{
    private List<DesktopBoxItem?>? _dropPreview;
    private Size? _previewSize;
    private int _dropIndex=-1,_dropSlots;
    private string? _dragIdentity;
    private string[]? _dragPaths;
    private List<DesktopBoxItem>? _dropBase;
    private readonly ExternalDragVisual _dropVisual=new();
    private readonly Canvas _dragOverlay=new() { IsHitTestVisible=false };
    private readonly Image _dragGhost=new() { Width=32,Height=32,Opacity=.92,Visibility=Visibility.Collapsed };
    private int _ghostRevision;
    private DispatcherTimer? _dragScroll;
    private Point _dragPoint;
    internal IReadOnlyList<string?> DisplayItemIds=>(_dropPreview??_box.Items.Cast<DesktopBoxItem?>()).Select(item=>item?.Id).ToArray();
    internal int MovingItemCount=>_itemMotion.Count;

    private void InitializeItemDragging()
    {
        _dragOverlay.Children.Add(_dragGhost); ContentLayer.Children.Add(_dragOverlay);
        ContentLayer.DragEnter+=OverItems; ContentLayer.DragOver+=OverItems;
        ContentLayer.DragLeave+=(_,e)=> {
            if(!GetCursorPos(out var pointer)) { ClearItemDragPreview(); return; }
            var p=ContentLayer.PointFromScreen(new Point(pointer.X,pointer.Y));
            if(p.X<0 || p.Y<0 || p.X>ContentLayer.ActualWidth || p.Y>ContentLayer.ActualHeight)ClearItemDragPreview();
        };
        ContentLayer.Drop+=DropItems;
    }
    private void OverItems(object sender,DragEventArgs e)
    {
        _magnetism.Reset();
        e.Handled=true;
        var paths=DesktopShellReference.ReadPaths(e.Data);
        var identity=e.Data.GetData("GlassDock.BoxReference") as string;
        e.Effects=paths is {Length:>0}?ReferenceEffect(e.AllowedEffects):DragDropEffects.None;
        if(e.Effects==DragDropEffects.None) { ClearItemDragPreview(); return; }
        _hideTimer.Stop(); _manager.RevealGroup();
        if(!TrackItemDragTarget(paths,identity,e.GetPosition(_items))) { e.Effects=DragDropEffects.None; ClearItemDragPreview(); return; }
        _dragPoint=e.GetPosition(_items);
        _dropVisual.Update(this,ContentLayer,e,prepareIcons:false);
        if(identity is not null)
        {
            var point=e.GetPosition(ContentLayer); Canvas.SetLeft(_dragGhost,point.X+9); Canvas.SetTop(_dragGhost,point.Y+9);
            _dragGhost.Visibility=Visibility.Visible;
        }
        _dragScroll??=new DispatcherTimer(DispatcherPriority.Input) { Interval=TimeSpan.FromMilliseconds(80) };
        if(!_dragScroll.IsEnabled) { _dragScroll.Tick-=ScrollWhileDragging; _dragScroll.Tick+=ScrollWhileDragging; _dragScroll.Start(); }
    }
    private void ScrollWhileDragging(object? sender,EventArgs e)
    {
        if((GetAsyncKeyState(1)&0x8000)==0 || (GetAsyncKeyState(0x1B)&0x8000)!=0 || !GetCursorPos(out var pointer)) { ClearItemDragPreview(); return; }
        var local=ContentLayer.PointFromScreen(new Point(pointer.X,pointer.Y));
        if(local.X<0 || local.Y<0 || local.X>ContentLayer.ActualWidth || local.Y>ContentLayer.ActualHeight) { ClearItemDragPreview(); return; }
        if(_dragPaths is null)return;
        _dragPoint=_items.PointFromScreen(new Point(pointer.X,pointer.Y));
        var scroll=Descendants<ScrollViewer>(_items).FirstOrDefault();
        if(scroll is null)return;
        double step=_dragPoint.Y<22?-18:_dragPoint.Y>_items.ActualHeight-22?18:0;
        if(step==0)return;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset+step);
        if(_dropPreview is not null)PreviewItemInsertion(_dragPaths,_dragIdentity,_dragPoint);
    }
    internal bool TrackItemDragTarget(string[] paths,string? identity,Point point)
    {
        _manager.ClaimItemDragTarget(this);
        if(identity?.Split(':') is [var source,_] && source==_box.Id)
        { PreviewItemInsertion(paths,identity,point); return _dropSlots>0; }
        // Passing over another box never changes its grid or dimensions. Its real
        // insertion slot is calculated only when the pointer releases the item.
        if(_dropPreview is not null)ClearItemDragPreview();
        _manager.ClaimItemDragTarget(this);
        var known=_box.Items.Select(item=>item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(_box.Items.Count>=500 || !paths.Any(path=>!known.Contains(path)))return false;
        bool changed=_dragPaths is null || _dragIdentity!=identity || !_dragPaths.SequenceEqual(paths,StringComparer.OrdinalIgnoreCase);
        _dragPaths=paths; _dragIdentity=identity; _dragPoint=point;
        if(changed && identity is not null)_=LoadDragGhost(paths[0],++_ghostRevision);
        return true;
    }
    private int InsertionIndexAt(Point point,int count)
    {
        int columns=Math.Max(1,_rowColumns);
        var scroll=Descendants<ScrollViewer>(_items).FirstOrDefault();
        double cellWidth=Math.Max(1,_items.ActualWidth/columns);
        int column=Math.Clamp((int)Math.Floor((point.X+cellWidth/2)/cellWidth),0,columns);
        int row=Math.Max(0,(int)Math.Floor((point.Y+(scroll?.VerticalOffset??0))/RowHeight));
        return Math.Clamp(row*columns+column,0,count);
    }
    internal void PreviewItemInsertion(string[] paths,string? identity,Point point,int? explicitIndex=null)
    {
        if(_closed)return;
        bool changedSource=_dropBase is null || _dragIdentity!=identity || _dragPaths is null || !_dragPaths.SequenceEqual(paths,StringComparer.OrdinalIgnoreCase);
        if(changedSource)
        {
            _dragIdentity=identity; _dragPaths=paths;
            _dropBase=_box.Items.ToList();
            if(identity?.Split(':') is [var source,var item] && source==_box.Id)
            {
                int removed=_dropBase.RemoveAll(value=>value.Id==item);
                _dropSlots=removed==1?1:0;
            }
            else
            {
                var known=_box.Items.Select(item=>item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _dropSlots=Math.Min(500-_box.Items.Count,paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(path=>!known.Contains(path)));
            }
            if(_dropSlots<=0) { ClearItemDragPreview(); return; }
            var screen=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
            var available=new Size((screen.Right-screen.Left)/dpi.DpiScaleX-32,(screen.Bottom-screen.Top)/dpi.DpiScaleY-140);
            int count=_dropBase.Count+_dropSlots;
            var adaptive=DesktopBoxLayout.AdaptiveSize(count,available);
            // Never shrink the target under the pointer. A manual box keeps its width,
            // but temporarily adds rows when the incoming references overflow it.
            double width=_box.AutoSize?Math.Max(_box.Width,adaptive.Width):_box.Width;
            int columns=Math.Max(2,(int)((width-24)/TileWidth));
            double height=Math.Min(Math.Max(_box.Height,ChromeHeight+Math.Ceiling(count/(double)columns)*RowHeight),Math.Max(_box.Height,Math.Min(720,available.Height)));
            _previewSize=new Size(width,height);
            if(identity is not null)_=LoadDragGhost(paths[0],++_ghostRevision);
        }
        var scroll=Descendants<ScrollViewer>(_items).FirstOrDefault();
        double cellWidth=Math.Max(1,(_previewSize!.Value.Width-32)/ColumnCount);
        int column=Math.Clamp((int)Math.Floor((point.X+cellWidth/2)/cellWidth),0,ColumnCount);
        int row=Math.Max(0,(int)Math.Floor((point.Y+(scroll?.VerticalOffset??0))/RowHeight));
        int index=Math.Clamp(explicitIndex??(row*ColumnCount+column),0,_dropBase!.Count);
        if(!changedSource && index==_dropIndex)return;
        var positions=CaptureItemPositions();
        _dropIndex=index; _dropPreview=_dropBase.Cast<DesktopBoxItem?>().ToList();
        _dropPreview.InsertRange(index,Enumerable.Repeat<DesktopBoxItem?>(null,_dropSlots));
        _empty.Visibility=Visibility.Hidden;
        Reposition(); AnimateItemReflow(positions);
    }
    private async Task LoadDragGhost(string path,int revision)
    {
        try { var icon=await Services.IconService.GetFileIconAsync(path,Math.Max(32,_iconPixels)); if(revision==_ghostRevision && !_closed)_dragGhost.Source=icon.Image; }
        catch { /* A source-provided Shell drag image can still remain available. */ }
    }
    private async void DropItems(object sender,DragEventArgs e)
    {
        e.Handled=true;
        var paths=DesktopShellReference.ReadPaths(e.Data);
        var identity=e.Data.GetData("GlassDock.BoxReference") as string;
        e.Effects=paths is {Length:>0}?ReferenceEffect(e.AllowedEffects):DragDropEffects.None;
        if(e.Effects==DragDropEffects.None) { ClearItemDragPreview(); return; }
        if(!PrepareItemDrop(paths,identity,e.GetPosition(_items))) { e.Effects=DragDropEffects.None; return; }
        _dropVisual.Drop(ContentLayer,e);
        await CommitItemInsertionAsync(paths,identity);
    }
    internal bool PrepareItemDrop(string[] paths,string? identity,Point point,int? explicitIndex=null)
    {
        _manager.ClaimItemDragTarget(this);
        bool wasCollapsed=!_expanded;
        if(wasCollapsed) { SetExpanded(true); UpdateLayout(); }
        _dragPoint=point;
        bool sameBox=identity?.Split(':') is [var source,_] && source==_box.Id;
        PreviewItemInsertion(paths,identity,point,explicitIndex??(wasCollapsed?_box.Items.Count:InsertionIndexAt(point,_box.Items.Count-(sameBox?1:0))));
        if(_dropSlots<=0) { ClearItemDragPreview(); if(wasCollapsed)SetExpanded(false); return false; }
        if(wasCollapsed)_box.Collapsed=false;
        return true;
    }
    internal async Task<bool> CommitItemInsertionAsync(string[] paths,string? identity)
    {
        if(_dropPreview is null || _dropSlots<=0)return false;
        int index=_dropIndex; var positions=CaptureItemPositions();
        var knownIds=_box.Items.Select(item=>item.Id).ToHashSet(StringComparer.Ordinal);
        var landingOrigin=_items.PointToScreen(_dragPoint);
        if(identity?.Split(':') is [_,var movingId])
            positions[movingId]=_dragGhost.Visibility==Visibility.Visible?_dragGhost.PointToScreen(new Point()):landingOrigin;
        // Drop commits once; leaving or cancelling only discards the temporary slots.
        if(!_box.AutoSize && _previewSize is Size expanded) { _box.Width=expanded.Width; _box.Height=expanded.Height; }
        ResetItemPreview();
        try
        {
            if(identity?.Split(':') is [var sourceId,var itemId])return _manager.MoveReference(_box,sourceId,itemId,index);
            await _manager.AddReferencesAsync(_box,paths,index); return true;
        }
        catch(Exception exception) { ShowFailure("添加失败",exception); return false; }
        finally
        {
            foreach(var item in _box.Items)if(!knownIds.Contains(item.Id))positions[item.Id]=landingOrigin;
            Reposition(); AnimateItemReflow(positions);
        }
    }
    private void ResetItemPreview()
    {
        _manager.ReleaseItemDragTarget(this);
        _dropPreview=null; _dropBase=null; _previewSize=null; _dropIndex=-1; _dropSlots=0;
        _dragPaths=null; _dragIdentity=null; _ghostRevision++; _dragGhost.Source=null; _dragGhost.Visibility=Visibility.Collapsed;
        _dragScroll?.Stop();
    }
    internal void ClearItemDragPreview()
    {
        _dropVisual.Leave();
        if(_dropPreview is null) { ResetItemPreview(); return; }
        var positions=CaptureItemPositions(); ResetItemPreview();
        if(_closed)return;
        _empty.Visibility=_box.Items.Count==0?Visibility.Visible:Visibility.Hidden;
        Reposition(); AnimateItemReflow(positions);
    }
    private void DisposeItemDragging()
    {
        _dragScroll?.Stop(); _dropVisual.Dispose(); ReleaseItemViews();
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
