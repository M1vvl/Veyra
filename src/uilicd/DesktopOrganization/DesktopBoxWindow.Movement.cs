using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxWindow
{
    private bool _moving,_resizing,_itemDragging;
    private Rect _dragOrigin;
    private NativePoint _moveCursor,_resizeCursor;
    private double _resizeWidth,_resizeHeight;
    private Point _resizeOrigin;
    private Vector _resizeCorner;
    private bool _resizeAuto;
    internal Rect VisibleBoundsPixels=>new(Root.PointToScreen(Surface.TopLeft),Root.PointToScreen(Surface.BottomRight));
    private void BeginMove(object sender,DragStartedEventArgs e)
    {
        if(_moving || _resizing || !GetCursorPos(out _moveCursor)) return;
        _magnetism.Reset(); _moving=true; _hideTimer.Stop(); _dragOrigin=VisibleBoundsPixels; e.Handled=true;
    }
    private void MoveBox(object sender,DragDeltaEventArgs e)
    {
        if(!_moving || !GetCursorPos(out var pointer)) return;
        var desired=_dragOrigin; desired.Offset(pointer.X-_moveCursor.X,pointer.Y-_moveCursor.Y);
        ApplyDragBounds(_manager.PreviewDrag(this,desired)); e.Handled=true;
    }
    internal void ApplyDragBounds(Rect bounds)
    {
        var origin=PlanDragBounds(bounds);
        var dpi=VisualTreeHelper.GetDpi(this);
        Left=origin.X/dpi.DpiScaleX; Top=origin.Y/dpi.DpiScaleY;
    }
    internal Point PlanDragBounds(Rect bounds)
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        var origin=new Point(bounds.Left-Surface.Left*dpi.DpiScaleX,bounds.Top-Surface.Top*dpi.DpiScaleY);
        _box.Left=origin.X/dpi.DpiScaleX+ShadowFrame; _box.Top=origin.Y/dpi.DpiScaleY+ShadowFrame;
        return origin;
    }
    internal void PreviewMoveForProbe(Rect desired)
    {
        _moving=true; ApplyDragBounds(_manager.PreviewDrag(this,desired));
    }
    internal void FinishMoveForProbe()
    {
        _moving=false; _manager.EndDrag(); _manager.Update(_box); UpdateDesktopLayer();
    }
    private void FinishMove(object sender,DragCompletedEventArgs e)
    {
        if(!_moving) return;
        try { if(e.Canceled) ApplyDragBounds(_dragOrigin); else _manager.Update(_box); }
        finally { _moving=false; _manager.EndDrag(); UpdateDesktopLayer(); if(!_box.Pinned && !_hover) _hideTimer.Start(); }
        e.Handled=true;
    }
    private void BeginResize(object sender,DragStartedEventArgs e)
    {
        if(!_expanded && (sender as FrameworkElement)?.Tag is Vector edge && (edge.X==0 || edge.Y==0))return;
        _magnetism.Reset(); _resizing=GetCursorPos(out _resizeCursor); _resizeWidth=_box.Width; _resizeHeight=_box.Height; _resizeAuto=_box.AutoSize;
        _resizeOrigin=new Point(_box.Left,_box.Top); _resizeCorner=(sender as FrameworkElement)?.Tag is Vector corner?corner:new Vector(1,1);
        _hideTimer.Stop(); e.Handled=true;
    }
    private void ResizeBox(object sender,DragDeltaEventArgs e)
    {
        if(!_resizing || !GetCursorPos(out var pointer)) return;
        var dpi=VisualTreeHelper.GetDpi(this); _box.AutoSize=false;
        ApplyCornerResize(new Vector((pointer.X-_resizeCursor.X)/dpi.DpiScaleX,(pointer.Y-_resizeCursor.Y)/dpi.DpiScaleY));
        Reposition(); e.Handled=true;
    }
    private void ApplyCornerResize(Vector delta)
    {
        var monitor=DesktopNative.MonitorRect(this); var dpi=VisualTreeHelper.GetDpi(this);
        double availableWidth=_resizeCorner.X<0?_resizeOrigin.X+_resizeWidth-monitor.Left/dpi.DpiScaleX-6:monitor.Right/dpi.DpiScaleX-6-_resizeOrigin.X;
        double availableHeight=_resizeCorner.Y<0?_resizeOrigin.Y+_resizeHeight-monitor.Top/dpi.DpiScaleY-6:monitor.Bottom/dpi.DpiScaleY-6-_resizeOrigin.Y;
        var size=DesktopBoxLayout.ResizeSize(new Size(Math.Max(0,_resizeWidth+delta.X*_resizeCorner.X),Math.Max(0,_resizeHeight+delta.Y*_resizeCorner.Y)),new Size(Math.Max(0,availableWidth),Math.Max(0,availableHeight)));
        _box.Width=_resizeCorner.X==0?_resizeWidth:size.Width; _box.Height=_resizeCorner.Y==0?_resizeHeight:size.Height;
        _box.Left=_resizeOrigin.X+(_resizeCorner.X<0?_resizeWidth-_box.Width:0);
        _box.Top=_resizeOrigin.Y+(_resizeCorner.Y<0?_resizeHeight-_box.Height:0);
    }
    internal void PreviewCornerResize(Vector corner,Vector delta,bool cancel=false)
    {
        _resizeWidth=_box.Width; _resizeHeight=_box.Height; _resizeOrigin=new Point(_box.Left,_box.Top); _resizeAuto=_box.AutoSize;
        _resizeCorner=corner; _resizing=true; _box.AutoSize=false; ApplyCornerResize(delta);
        if(cancel) { _box.Width=_resizeWidth; _box.Height=_resizeHeight; _box.Left=_resizeOrigin.X; _box.Top=_resizeOrigin.Y; _box.AutoSize=_resizeAuto; }
        Reposition(); _resizing=false; _manager.Update(_box);
    }
    private void FinishResize(object sender,DragCompletedEventArgs e)
    {
        if(!_resizing) return;
        if(e.Canceled) { _box.Width=_resizeWidth; _box.Height=_resizeHeight; _box.Left=_resizeOrigin.X; _box.Top=_resizeOrigin.Y; _box.AutoSize=_resizeAuto; }
        _resizing=false; _manager.Update(_box); UpdateDesktopLayer(); e.Handled=true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
