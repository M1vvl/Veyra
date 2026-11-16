using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GlassDock;

internal static class DesktopNative
{
    private static nint _desktopHost;
    private static readonly HashSet<nint> DesktopWindows=new();
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint processId);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    private delegate bool EnumWindowsCallback(nint hwnd,nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback,nint parameter);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,uint attribute,out int value,int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    internal delegate void WinEventCallback(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll", SetLastError=true)] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    internal static nint WatchForeground(WinEventCallback callback) => SetWinEventHook(3,3,0,callback,0,0,0);
    internal static nint ForegroundForDesktopPolicy()=>ResolveForegroundForDesktopPolicy(GetForegroundWindow());
    internal static nint ResolveForegroundForDesktopPolicy(nint foreground)
    {
        bool Usable(nint window)=>window!=0 && IsWindowVisible(window) && !IsIconic(window)
            && !(DwmGetWindowAttribute(window,14,out int cloaked,4)==0 && cloaked!=0)
            && Rendering.NativeWindowComposition.Read(window).Visible;
        if(Usable(foreground))return foreground;
        // Alt+Tab / Show Desktop can leave a minimized or closed shell host as
        // foreground until the next click. Resolve the actual visible Z order.
        for(nint window=GetTopWindow(0),count=0;window!=0 && count<4096;window=GetWindow(window,2),count++)
        {
            if(!Usable(window) || IsCurrentProcessWindow(window))continue;
            var name=new System.Text.StringBuilder(100); GetClassName(window,name,name.Capacity);
            if(name.ToString() is "Progman" or "WorkerW")return window;
            if((GetWindowLong(window,-20)&0x80)!=0 || name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "#32768")continue;
            return window;
        }
        return GetShellWindow();
    }
    internal static void EnsureNativeShown(Window window,bool expectedVisible=false)
    {
        if(expectedVisible && !window.IsVisible)window.Show();
        if(window.Visibility!=Visibility.Visible)return;
        nint handle=new WindowInteropHelper(window).Handle;
        if(handle!=0 && IsCurrentProcessWindow(handle) && (!IsWindowVisible(handle) || IsIconic(handle)))ShowWindow(handle,4);
    }
    internal static bool ForegroundCoversMonitor(Window surface)
    {
        nint foreground=ForegroundForDesktopPolicy();
        if(foreground==0 || IsCurrentProcessWindow(foreground))return false;
        var root=GetAncestor(foreground,2); if(root==0)root=foreground;
        var name=new System.Text.StringBuilder(100); GetClassName(root,name,name.Capacity);
        if(name.ToString() is "Progman" or "WorkerW")return false;
        var monitor=MonitorRect(surface);
        return IsZoomed(root) && MonitorFromWindow(root,2)==MonitorFromWindow(new WindowInteropHelper(surface).Handle,2)
            || GetWindowRect(root,out var bounds) && bounds.Left<=monitor.Left+1 && bounds.Top<=monitor.Top+1 && bounds.Right>=monitor.Right-1 && bounds.Bottom>=monitor.Bottom-1;
    }
    internal static void PlaceFloatingDesktopSurface(Window window)
    {
        if(ForegroundCoversMonitor(window)) {if(window.Topmost)window.Topmost=false; PlaceOnDesktop(window); return;}
        if(!window.Topmost)window.Topmost=true;
        nint handle=new WindowInteropHelper(window).Handle,foreground=ForegroundForDesktopPolicy();
        if(foreground!=0 && !IsCurrentProcessWindow(foreground) && IsAbove(foreground,handle))SetWindowPos(handle,-1,0,0,0,0,0x0013);
        EnsureNativeShown(window);
    }
    internal static void PlaceAlwaysOnTop(Window window)
    {
        if(!window.Topmost)window.Topmost=true;
        nint handle=new WindowInteropHelper(window).Handle;
        if(handle!=0 && GetWindowRect(handle,out var rail))
        {
            bool covered=(GetWindowLong(handle,-20)&8)==0;
            for(nint above=GetWindow(handle,3),count=0;!covered && above!=0 && count<4096;above=GetWindow(above,3),count++)
            {
                if(IsCurrentProcessWindow(above) || !IsWindowVisible(above) || IsIconic(above))continue;
                if(DwmGetWindowAttribute(above,14,out int cloaked,4)==0 && cloaked!=0)continue;
                if(!Rendering.NativeWindowComposition.Read(above).Visible)continue;
                covered=GetWindowRect(above,out var bounds) && bounds.Left<rail.Right && bounds.Right>rail.Left
                    && bounds.Top<rail.Bottom && bounds.Bottom>rail.Top;
            }
            if(covered)SetWindowPos(handle,-1,0,0,0,0,0x0013);
        }
        EnsureNativeShown(window,true);
    }
    private static bool IsAbove(nint candidate,nint surface)
    {
        for(nint window=GetTopWindow(0),count=0;window!=0 && count<4096;window=GetWindow(window,2),count++)
        {if(window==surface)return false; if(window==candidate)return true;}
        return false;
    }
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    private static nint FindDesktopHost()
    {
        if(IsWindow(_desktopHost) && FindWindowEx(_desktopHost,0,"SHELLDLL_DefView",null)!=0) return _desktopHost;
        nint host=0;
        EnumWindows((hwnd,_)=> {
            if(FindWindowEx(hwnd,0,"SHELLDLL_DefView",null)==0) return true;
            host=hwnd;
            return false;
        },0);
        _desktopHost=host!=0?host:GetShellWindow();
        return _desktopHost;
    }
    internal static void PlaceOnDesktop(Window dock)
    {
        var desktop=FindDesktopHost();
        if(desktop==0) return;
        var dockHandle=new WindowInteropHelper(dock).Handle;
        if(dockHandle==0 || !IsCurrentProcessWindow(dockHandle)) return;
        if(DesktopWindows.Add(dockHandle)) dock.Closed+=(_,_)=>DesktopWindows.Remove(dockHandle);
        if(IsIconic(dockHandle)) ShowWindow(dockHandle,4); // Restore without activating the Dock.
        // Any member of our contiguous group above the desktop is already placed.
        // Requiring each member to be adjacent would continuously reorder the group.
        if(IsInDesktopGroup(dockHandle,desktop))
        {
            if(!IsWindowVisible(dockHandle)) SetWindowPos(dockHandle,0,0,0,0,0,0x0057);
            return;
        }
        var insertAfter=GetWindow(desktop,3); // The window immediately above the desktop.
        bool topmost=(GetWindowLong(dockHandle,-20)&0x8)!=0;
        if(insertAfter==dockHandle) insertAfter=GetWindow(dockHandle,3);
        if(topmost) SetWindowPos(dockHandle,-2,0,0,0,0,0x0053);
        // A topmost anchor would promote the Dock into that band. HWND_TOP keeps it ordinary.
        if(insertAfter!=0 && (GetWindowLong(insertAfter,-20)&0x8)!=0) insertAfter=0;
        SetWindowPos(dockHandle,insertAfter,0,0,0,0,0x0053);
    }
    internal static bool IsAtDesktopLayer(Window dock)
    {
        var handle=new WindowInteropHelper(dock).Handle;
        var desktop=FindDesktopHost();
        return desktop!=0 && IsWindowVisible(handle) && !IsIconic(handle) && IsInDesktopGroup(handle,desktop);
    }
    private static bool IsCurrentProcessWindow(nint handle)
    {
        if(!IsWindow(handle)) return false;
        GetWindowThreadProcessId(handle,out var processId);
        return processId==(uint)Environment.ProcessId;
    }
    private static bool IsRegisteredDesktopWindow(nint handle)
    {
        if(!DesktopWindows.Contains(handle)) return false;
        if(IsCurrentProcessWindow(handle)) return true;
        DesktopWindows.Remove(handle);
        return false;
    }
    private static bool IsHiddenDesktopOwner(nint handle)
    {
        if(IsWindowVisible(handle) || !IsCurrentProcessWindow(handle)) return false;
        foreach(var surface in DesktopWindows)
            if(IsCurrentProcessWindow(surface) && GetWindow(surface,4)==handle) return true;
        return false;
    }
    private static bool IsInDesktopGroup(nint handle,nint desktop)
    {
        if(!IsRegisteredDesktopWindow(handle) || (GetWindowLong(handle,-20)&0x8)!=0) return false;
        // WPF creates an invisible owner for each ShowInTaskbar=false window.
        // Accept only the exact hidden owners of registered desktop surfaces.
        int remaining=DesktopWindows.Count*2;
        while(remaining-->0)
        {
            handle=GetWindow(handle,2);
            if(handle==desktop) return true;
            if((!IsRegisteredDesktopWindow(handle) && !IsHiddenDesktopOwner(handle)) || (GetWindowLong(handle,-20)&0x8)!=0) return false;
        }
        return false;
    }
    internal static bool IsCoveredByForeground(Window dock)
    {
        var foreground=ForegroundForDesktopPolicy();
        var dockHandle=new WindowInteropHelper(dock).Handle;
        if(foreground==0 || foreground==dockHandle || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
        if((GetWindowLong(foreground,-20)&0x80)!=0) return false;
        if(DwmGetWindowAttribute(foreground,14,out var cloaked,sizeof(int))==0 && cloaked!=0) return false;
        var name=new System.Text.StringBuilder(128);
        GetClassName(GetAncestor(foreground,2),name,name.Capacity);
        if(name.ToString() is "Progman" or "WorkerW") return false;
        return GetWindowRect(foreground,out var cover) && GetWindowRect(dockHandle,out var bounds)
            && cover.Left<=bounds.Left && cover.Top<=bounds.Top && cover.Right>=bounds.Right && cover.Bottom>=bounds.Bottom;
    }
    internal static Rect MonitorRect(Window window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 1), ref info);
        return info.Monitor;
    }
    internal static double BottomTaskbarClearance(Window window)
    {
        var monitor=MonitorRect(window);
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window);
        // Autohide may reduce the work-area inset to 1–2px, so read the actual
        // Explorer bar height. Our notification receiver has the same class name;
        // process identity keeps it out of this geometry calculation.
        GetWindowThreadProcessId(GetShellWindow(),out uint explorerProcess);
        double barHeight=48*dpi.DpiScaleY;
        EnumWindows((handle,_)=>
        {
            GetWindowThreadProcessId(handle,out uint process);
            if(process!=explorerProcess || process==0) return true;
            var name=new System.Text.StringBuilder(64); GetClassName(handle,name,name.Capacity);
            if(name.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd") || !GetWindowRect(handle,out var bounds)) return true;
            int height=bounds.Bottom-bounds.Top,width=bounds.Right-bounds.Left;
            if(width<height || width<monitor.Right-monitor.Left-32 || bounds.Left>=monitor.Right || bounds.Right<=monitor.Left) return true;
            if(Math.Abs(bounds.Top-monitor.Bottom)>height+16 && Math.Abs(bounds.Bottom-monitor.Bottom)>height+16) return true;
            if(height>0 && height<(monitor.Bottom-monitor.Top)/4) barHeight=Math.Max(barHeight,height);
            return true;
        },0);
        return barHeight/dpi.DpiScaleY+12;
    }
    internal static void Configure(Window window,bool noActivate=true)
    {
        nint h = new WindowInteropHelper(window).Handle;
        var extended=GetWindowLong(h,-20) | 0x80;
        SetWindowLong(h,-20,noActivate?extended|0x08000000:extended&~0x08000000);
        // Capture exclusion is global, including the user's screenshot/recording tools.
        // Internal optical capture omits our HWNDs in its own background scene instead.
        if (!SetWindowDisplayAffinity(h, 0)) Program.Log("Allow screen capture: " + Marshal.GetLastWin32Error());
    }
    internal static bool IsFullscreen(Window window)
    {
        var h = GetForegroundWindow();
        if (h == 0 || h == new WindowInteropHelper(window).Handle) return false;
        if(IsZoomed(h)) return false; // Maximized ordinary apps still need their replacement taskbar.
        var cls = new System.Text.StringBuilder(128); GetClassName(h, cls, 128);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        var monitor = MonitorRect(window);
        return GetWindowRect(h, out var r) && r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }
}
