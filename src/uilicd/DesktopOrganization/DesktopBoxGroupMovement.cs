using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxGroupController
{
    private bool _dragging;
    private NativePoint _groupCursor;
    private Rect _groupHandleOrigin,_groupBounds;
    private (DesktopBoxWindow Window,Rect Bounds)[] _groupOrigins=[];
    private DesktopWindowMove[] _groupMoves=[];
    private bool _movePending;
    private Vector _pendingMove;
    private long _moveRequests,_moveFrames;
    internal bool Dragging=>_dragging;
    internal void BeginHandleDrag(object sender,DragStartedEventArgs e)
    {
        if(_dragging || !GetCursorPos(out _groupCursor))return;
        Show(); _dragging=true; _hoverTimer.Stop(); _groupHandleOrigin=_handle.ScreenBounds;
        _groupOrigins=_manager.DesktopWindows.Select(window=>(window,window.VisibleBoundsPixels)).ToArray();
        _groupMoves=new DesktopWindowMove[_groupOrigins.Length+1]; _movePending=false;
        CompositionTarget.Rendering+=ApplyPendingGroupMove;
        _groupBounds=Rect.Empty; foreach(var item in _groupOrigins)_groupBounds.Union(item.Bounds);
        e.Handled=true;
    }
    internal void MoveHandleDrag(object sender,DragDeltaEventArgs e)
    {
        if(!_dragging || !GetCursorPos(out var cursor))return;
        RequestHandleMove(cursor.X-_groupCursor.X,cursor.Y-_groupCursor.Y); e.Handled=true;
    }
    private void RequestHandleMove(double dx,double dy) { _pendingMove=new Vector(dx,dy); _movePending=true; _moveRequests++; }
    private void ApplyPendingGroupMove(object? sender,EventArgs args)=>FlushGroupMove();
    private void FlushGroupMove()
    {
        if(!_dragging || !_movePending)return;
        var move=_pendingMove; _movePending=false; PreviewHandleMove(move.X,move.Y);
    }
    private void StopGroupMove()
    {
        CompositionTarget.Rendering-=ApplyPendingGroupMove; _movePending=false; _groupMoves=[];
    }
    private void PreviewHandleMove(double dx,double dy)
    {
        var monitor=DesktopNative.MonitorRect(_handle);
        var control=_groupHandleOrigin; control.Offset(dx,dy);
        control.X=Math.Clamp(control.X,monitor.Left+4,Math.Max(monitor.Left+4,monitor.Right-control.Width-4));
        control.Y=Math.Clamp(control.Y,monitor.Top+4,Math.Max(monitor.Top+4,monitor.Bottom-control.Height-4));
        var dpi=VisualTreeHelper.GetDpi(_handle);
        var glass=control; glass.Inflate(-6*dpi.DpiScaleX,-6*dpi.DpiScaleY);
        double nearTop=Math.Abs(control.Top-monitor.Top),nearLeft=Math.Abs(control.Left-monitor.Left),nearRight=Math.Abs(control.Right-monitor.Right);
        double nearest=Math.Min(nearTop,Math.Min(nearLeft,nearRight));
        string edge=nearest>24*dpi.DpiScaleX?"Free":nearest==nearTop?"Top":nearest==nearLeft?"Left":"Right";
        var area=PlacementArea(glass,_groupBounds.Size,edge);
        double boxDx=Math.Clamp(_groupBounds.Left+dx,area.Left,Math.Max(area.Left,area.Right-_groupBounds.Width))-_groupBounds.Left;
        double boxDy=Math.Clamp(_groupBounds.Top+dy,area.Top,Math.Max(area.Top,area.Bottom-_groupBounds.Height))-_groupBounds.Top;
        _groupMoves[0]=new(_handle,control.TopLeft);
        for(int index=0;index<_groupOrigins.Length;index++)
        {
            var item=_groupOrigins[index]; var bounds=item.Bounds; bounds.Offset(boxDx,boxDy);
            _groupMoves[index+1]=new(item.Window,item.Window.PlanDragBounds(bounds));
        }
        if(!DesktopWindowMotion.Apply(_groupMoves))
        {
            _handle.MoveControlPixels(control.TopLeft);
            foreach(var item in _groupOrigins) { var bounds=item.Bounds; bounds.Offset(boxDx,boxDy); item.Window.ApplyDragBounds(bounds); }
        }
        _moveFrames++;
    }
    internal void EndHandleDrag(object sender,DragCompletedEventArgs e)
    {
        if(!_dragging)return;
        if(!e.Canceled)FlushGroupMove();
        StopGroupMove();
        if(e.Canceled)
        {
            _handle.MoveControlPixels(_groupHandleOrigin.TopLeft);
            foreach(var item in _groupOrigins)item.Window.ApplyDragBounds(item.Bounds);
        }
        _dragging=false; _groupOrigins=[]; _lastInside=Environment.TickCount64;
        if(!e.Canceled)
        {
            var bounds=_handle.ScreenBounds; var monitor=DesktopNative.MonitorRect(_handle); var dpi=VisualTreeHelper.GetDpi(_handle);
            double top=Math.Abs(bounds.Top-monitor.Top),left=Math.Abs(bounds.Left-monitor.Left),right=Math.Abs(bounds.Right-monitor.Right);
            double nearest=Math.Min(top,Math.Min(left,right));
            _manager.Configuration.GroupEdge=nearest>24*dpi.DpiScaleX?"Free":nearest==top?"Top":nearest==left?"Left":"Right";
            _manager.Configuration.GroupLeft=_handle.Left; _manager.Configuration.GroupTop=_handle.Top;
            _handle.Refresh(); _manager.Configuration.GroupLeft=_handle.Left; _manager.Configuration.GroupTop=_handle.Top;
            EnsureSpacing(); _manager.CommitLayout();
        }
        if(!Pinned)_hoverTimer.Start(); e.Handled=true;
    }
    internal void PreviewHandleDragForProbe(Vector offset,bool cancel=false)
    {
        BeginHandleDrag(_handle,new DragStartedEventArgs(0,0));
        if(!_dragging)return; PreviewHandleMove(offset.X,offset.Y);
        EndHandleDrag(_handle,new DragCompletedEventArgs(offset.X,offset.Y,cancel));
    }
    internal void BeginRapidDragForProbe()=>BeginHandleDrag(_handle,new DragStartedEventArgs(0,0));
    internal void MoveRapidDragForProbe(Vector offset)=>RequestHandleMove(offset.X,offset.Y);
    internal void EndRapidDragForProbe()=>EndHandleDrag(_handle,new DragCompletedEventArgs(0,0,false));
    internal (long Requests,long Frames) DragCounters=>(_moveRequests,_moveFrames);
}
