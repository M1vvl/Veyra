using System.Windows;
using System.Windows.Media;

namespace GlassDock.DesktopOrganization;

internal sealed partial class DesktopBoxGroupController
{
    private bool _spacing;
    internal Rect ChildPlacementArea
    {
        get
        {
            Rect union=Rect.Empty; foreach(var box in _manager.DesktopWindows)union.Union(box.VisibleBoundsPixels);
            return PlacementArea(_handle.FullGlassBoundsPixels,union.IsEmpty?new Size():union.Size,Edge);
        }
    }
    internal Rect ControlGlassBounds=>_handle.FullGlassBoundsPixels;
    internal Rect ControlSpacingBounds
    {
        get
        {
            var bounds=ControlGlassBounds; var dpi=VisualTreeHelper.GetDpi(_handle);
            bounds.Inflate(-(DesktopBoxSpacing.Gap-DesktopBoxSpacing.ControlGap)*dpi.DpiScaleX,-(DesktopBoxSpacing.Gap-DesktopBoxSpacing.ControlGap)*dpi.DpiScaleY);
            return bounds;
        }
    }
    private Rect PlacementArea(Rect control,Size extent,string edge)
    {
        var monitor=DesktopNative.MonitorRect(_handle); var dpi=VisualTreeHelper.GetDpi(_handle);
        var screen=new Rect(monitor.Left+8*dpi.DpiScaleX,monitor.Top+8*dpi.DpiScaleY,
            Math.Max(1,monitor.Right-monitor.Left-16*dpi.DpiScaleX),Math.Max(1,monitor.Bottom-monitor.Top-16*dpi.DpiScaleY));
        return DesktopBoxSpacing.ChildArea(screen,control,extent,new Size(DesktopBoxSpacing.ControlGap*dpi.DpiScaleX,DesktopBoxSpacing.ControlGap*dpi.DpiScaleY),edge);
    }
    internal void EnsureSpacing()
    {
        if(_disposed || Dragging || _spacing || !_handle.IsLoaded)return;
        var windows=_manager.DesktopWindows.OrderBy(box=>box.VisibleBoundsPixels.Top).ThenBy(box=>box.VisibleBoundsPixels.Left).ToArray();
        if(windows.Length==0)return;
        _spacing=true;
        try
        {
            var dpi=VisualTreeHelper.GetDpi(_handle); var gap=new Size(DesktopBoxSpacing.Gap*dpi.DpiScaleX,DesktopBoxSpacing.Gap*dpi.DpiScaleY);
            var area=ChildPlacementArea; var obstacles=new List<Rect> { ControlSpacingBounds }; bool moved=false;
            Rect union=Rect.Empty; foreach(var window in windows)union.Union(window.VisibleBoundsPixels);
            double dx=union.Width<=area.Width?Math.Clamp(union.Left,area.Left,area.Right-union.Width)-union.Left:0;
            double dy=union.Height<=area.Height?Math.Clamp(union.Top,area.Top,area.Bottom-union.Height)-union.Top:0;
            foreach(var window in windows)
            {
                var before=window.VisibleBoundsPixels; var desired=before; desired.Offset(dx,dy);
                if(!DesktopBoxSpacing.TryPlace(desired,obstacles.ToArray(),area,gap,out var placed))placed=before;
                if(Math.Abs(placed.Left-before.Left)>.1 || Math.Abs(placed.Top-before.Top)>.1) { window.ApplyDragBounds(placed); moved=true; }
                obstacles.Add(placed);
            }
            if(moved)_manager.PersistLayout();
        }
        finally { _spacing=false; }
    }
}
