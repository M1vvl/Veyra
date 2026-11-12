using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassDock.DesktopOrganization;

/// <summary>One desktop-edge entry and one lifecycle for all mapping boxes.</summary>
internal sealed partial class DesktopBoxGroupController : IDisposable
{
    private readonly DesktopBoxManager _manager;
    private readonly DesktopBoxGroupHandle _handle;
    private readonly DispatcherTimer _hoverTimer,_layerTimer;
    private bool _shown=true,_anchored,_disposed,_manualHide;
    private bool _desktopSuspended;
    private int _holds;
    private readonly DesktopGroupPresentation _presentation=new();
    private long _lastInside;
    internal bool Shown=>_shown;
    internal bool Pinned=>_manager.Configuration.GroupPinned;
    internal string Edge=>_manager.Configuration.GroupEdge;
    internal Window ControlWindow=>_handle;
    internal bool Revealing=>_presentation.Revealing;
    internal long RevealFrame=>_presentation.CommitFrame;
    internal void PrepareReveal()
    {
        if(!_shown || _disposed)return;
        _presentation.Begin(_manager.DesktopWindows.Cast<GlassWindow>().Prepend(_handle),()=>_shown && !_desktopSuspended && !_disposed,()=>_lastInside=Environment.TickCount64);
    }
    internal void SetDesktopSuspended(bool suspended)
    {
        if(_desktopSuspended==suspended || _disposed)return;
        _desktopSuspended=suspended;
        if(suspended) { _presentation.Cancel(); _hoverTimer.Stop(); _layerTimer.Stop(); _handle.SuspendDesktop(); }
        else
        {
            _layerTimer.Start();
            _handle.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=> { if(!_disposed && !_desktopSuspended)Sync(); }));
        }
    }
    internal DesktopBoxGroupController(DesktopBoxManager manager)
    {
        _manager=manager; _handle=new DesktopBoxGroupHandle(this,manager);
        _shown=manager.Configuration.GroupPinned;
        _hoverTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) }; _hoverTimer.Tick+=(_,_)=>Poll();
        _layerTimer=new DispatcherTimer(DispatcherPriority.Background) { Interval=TimeSpan.FromMilliseconds(750) };
        _layerTimer.Tick+=(_,_)=> { if(Dragging)return; foreach(var box in _manager.DesktopWindows)box.UpdateDesktopLayer(); _handle.UpdateDesktopLayer(); };
        _manager.Changed+=Changed; _layerTimer.Start(); Sync();
    }
    private void Changed(object? sender,EventArgs e)=>Sync();
    internal void Sync()
    {
        if(_disposed) return;
        if(Dragging)return;
        if(_desktopSuspended)return;
        if(!_manager.Configuration.Enabled || !_manager.DesktopWindows.Any()) { _handle.Hide(); _hoverTimer.Stop(); return; }
        if(!_handle.IsVisible)_handle.Show(); _handle.Refresh();
        if(!_anchored) { if(_manager.Configuration.GroupLeft is null)Anchor(); _anchored=true; }
        foreach(var box in _manager.DesktopWindows)box.SetGroupShown(_shown);
        EnsureSpacing();
        if(Pinned) { Show(); _hoverTimer.Stop(); }
        else _hoverTimer.Start();
        _handle.SetCompact(!_shown);
    }
    internal void SetEdge(string edge)
    {
        if(edge is not ("Top" or "Left" or "Right"))return;
        _manager.Configuration.GroupEdge=edge; _manager.Configuration.GroupLeft=_manager.Configuration.GroupTop=null;
        Anchor(); _manager.Update(); _handle.Refresh();
    }
    private void Anchor()
    {
        var windows=_manager.DesktopWindows.ToArray(); if(windows.Length==0 || !_handle.IsLoaded)return;
        Rect union=Rect.Empty; foreach(var window in windows)union.Union(window.VisibleBoundsPixels);
        var monitor=DesktopNative.MonitorRect(_handle); double x=union.Left,y=union.Top;
        var dpi=VisualTreeHelper.GetDpi(_handle);
        if(Edge=="Top") { x=monitor.Right-union.Width-90*dpi.DpiScaleX; y=monitor.Top+54*dpi.DpiScaleY; }
        else
        {
            x=Edge=="Left"?monitor.Left+54*dpi.DpiScaleX:monitor.Right-union.Width-90*dpi.DpiScaleX;
            y=monitor.Top+(monitor.Bottom-monitor.Top-union.Height)/2;
        }
        x=Math.Clamp(x,monitor.Left+8,Math.Max(monitor.Left+8,monitor.Right-union.Width-8));
        y=Math.Clamp(y,monitor.Top+8,Math.Max(monitor.Top+8,monitor.Bottom-union.Height-100));
        foreach(var window in windows) { var bounds=window.VisibleBoundsPixels; bounds.Offset(x-union.Left,y-union.Top); window.ApplyDragBounds(bounds); }
    }
    internal void TogglePinned()
    {
        _manager.Configuration.GroupPinned=!Pinned;
        if(Pinned)Show(); else Hide(); _manager.Update();
    }
    internal void ToggleVisibility()
    {
        if(_shown) { _manager.Configuration.GroupPinned=false; Hide(); _manager.Update(); }
        else Show();
    }
    internal void SetCollapsed(bool collapsed)
    {
        _manager.SetCollapsed(_manager.Configuration.Boxes,collapsed); Show();
    }
    internal void EnterHandle() { if(!_manualHide)Show(); }
    internal void LeaveHandle() { _lastInside=Environment.TickCount64; }
    internal void Show()
    {
        if(_disposed || Dragging)return;
        _lastInside=Environment.TickCount64;
        if(_desktopSuspended) { _shown=true; _manualHide=false; foreach(var box in _manager.DesktopWindows)box.SetGroupShown(true); return; }
        if(_shown) { if(!Pinned)_hoverTimer.Start(); return; }
        _manualHide=false; _shown=true; PrepareReveal(); _handle.SetCompact(false);
        foreach(var box in _manager.DesktopWindows)box.SetGroupShown(true);
        EnsureSpacing();
        if(!Pinned)_hoverTimer.Start(); _handle.Refresh();
    }
    internal void Hide(bool blockUntilLeave=true)
    {
        if(_disposed || Dragging)return; _shown=false; _manualHide=blockUntilLeave;
        DesktopWindowMotion.SetVisible(_manager.DesktopWindows.Cast<Window>().ToArray(),false); _presentation.Cancel();
        foreach(var box in _manager.DesktopWindows)box.SetGroupShown(false);
        _handle.SetCompact(true); _handle.Refresh(); if(!Pinned && !_desktopSuspended)_hoverTimer.Start();
    }
    internal void Touch() { if(_shown)_lastInside=Environment.TickCount64; }
    internal void Hold(Window window)
    {
        _holds++; Show(); window.Closed+=(_,_)=> { _holds=Math.Max(0,_holds-1); _lastInside=Environment.TickCount64; if(!Pinned && !_desktopSuspended)_hoverTimer.Start(); };
    }
    private void Poll()
    {
        if(_disposed || _desktopSuspended || Pinned || Dragging || Program.CapturePreview)return;
        if(GetCursorPos(out var point))PollPointer(new Point(point.X,point.Y),Environment.TickCount64,false);
    }
    internal void PollPointerForProbe(Point cursor,long now)=>PollPointer(cursor,now,true);
    private void PollPointer(Point cursor,long now,bool probe)
    {
        if(_disposed || _desktopSuspended || Pinned || Dragging || _presentation.Revealing)return;
        bool inside=_holds>0 || _handle.MenuOpen || _manager.DesktopWindows.Any(box=>box.KeepsGroupVisible);
        {
            bool atHandle=_handle.ScreenBounds.Contains(cursor) && (probe || _handle.Topmost || !DesktopNative.IsCoveredByForeground(_handle));
            if(_manualHide)
            {
                if(!atHandle)_manualHide=false;
                return;
            }
            if(atHandle) { Show(); _lastInside=now; return; }
            inside|=_manager.DesktopWindows.Any(box=>box.IsVisible && box.DesktopPresentationVisible && box.VisibleBoundsPixels.Contains(cursor));
        }
        if(!_shown)return;
        if(inside) { _lastInside=now; return; }
        if(now-_lastInside>=450)Hide(false);
    }
    internal object Inspect()=>new { _shown,Pinned,Edge,Dragging,compact=_handle.Compact,holds=_holds,handle=_handle.IsVisible,moveRequests=_moveRequests,moveFrames=_moveFrames,hiddenMaterialCount=_manager.DesktopWindows.Count(box=>!box.IsVisible && box.HasMaterialResources),revealing=_presentation.Revealing,revealFrame=_presentation.CommitFrame,revealCount=_presentation.CommitCount };
    public void Dispose()
    {
        if(_disposed)return; _disposed=true; _presentation.Dispose(); StopGroupMove(); _hoverTimer.Stop(); _layerTimer.Stop(); _manager.Changed-=Changed; _handle.Close();
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
