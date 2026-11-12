using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace GlassDock;

/// <summary>Uses the Dock's existing foreground/geometry cadence; no extra polling loop.</summary>
internal sealed class DesktopResourceCoordinator(SidebarWindow sidebar,DesktopOrganization.DesktopBoxManager boxes)
{
    private nint _lastWindow;
    private DesktopNative.Rect _lastBounds;
    private bool _lastZoomed;
    private long _transitions;
    internal void Update(bool force=false)
    {
        if(Program.CapturePreview || boxes.InteractionBusy || sidebar.CapturedInteraction || Program.TrayMenus?.Active==true)return;
        nint foreground=DesktopNative.ForegroundForDesktopPolicy();
        if(foreground==0 || !DesktopNative.IsWindowVisible(foreground) || IsIconic(foreground))return;
        GetWindowThreadProcessId(foreground,out uint process);
        if(process==(uint)Environment.ProcessId)return;
        var root=GetAncestor(foreground,2); if(root==0)root=foreground;
        for(int depth=0;depth<8;depth++) { nint owner=GetWindow(root,4); if(owner==0 || !DesktopNative.IsWindowVisible(owner))break; root=owner; }
        var name=new StringBuilder(128); GetClassName(root,name,name.Capacity);
        string type=name.ToString();
        // A taskbar or Start detour over a game does not mean its desktop is visible.
        if(type is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")return;
        if(DwmGetWindowAttribute(root,14,out int cloaked,4)==0 && cloaked!=0)return;
        if(!GetWindowRect(root,out var bounds))return;
        bool zoomed=IsZoomed(root),desktop=type is "Progman" or "WorkerW";
        if(!force && root==_lastWindow && zoomed==_lastZoomed && bounds.Left==_lastBounds.Left && bounds.Top==_lastBounds.Top && bounds.Right==_lastBounds.Right && bounds.Bottom==_lastBounds.Bottom)
        {RecoverShellVisibility(); return;}
        _lastWindow=root; _lastZoomed=zoomed; _lastBounds=bounds;
        bool Covered(Window surface)
        {
            if(desktop)return false;
            var monitor=DesktopNative.MonitorRect(surface);
            return bounds.Left<=monitor.Left+1 && bounds.Top<=monitor.Top+1 && bounds.Right>=monitor.Right-1 && bounds.Bottom>=monitor.Bottom-1
                || zoomed && MonitorFromWindow(root,2)==MonitorFromWindow(new WindowInteropHelper(surface).Handle,2);
        }
        sidebar.SetDesktopSuspended(Covered(sidebar));
        if(boxes.GroupControl is Window handle)boxes.SetDesktopSuspended(Covered(handle));
        _transitions++;
        RecoverShellVisibility();
    }
    private void RecoverShellVisibility()
    {
        if(!sidebar.DesktopSuspended)DesktopNative.EnsureNativeShown(sidebar,true);
        if(!boxes.DesktopSuspended)
        {
            if(boxes.Configuration.Enabled && boxes.GroupControl is Window control)DesktopNative.EnsureNativeShown(control,true);
            foreach(var box in boxes.DesktopWindows)if(box.ExpectedVisible) {DesktopNative.EnsureNativeShown(box,true); box.UpdateDesktopLayer();}
        }
    }
    internal object Inspect()=>new { transitions=_transitions,foreground=_lastWindow.ToInt64(),sidebarPaused=sidebar.DesktopSuspended,boxesPaused=boxes.DesktopSuspended };
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out DesktopNative.Rect bounds);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint window,StringBuilder name,int length);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window,uint attribute,out int value,int size);
}
