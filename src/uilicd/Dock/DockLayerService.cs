using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassDock.Docking;

/// <summary>Floats glass over restored windows and yields to maximized/fullscreen apps.</summary>
internal sealed class DockLayerService : IDisposable
{
    private readonly Window _dock;
    private readonly DesktopNative.WinEventCallback _locationCallback;
    private nint _locationHook;
    private nint _foreground;
    private bool _allowOverlay=true;
    private bool _disposed;
    private bool _updateQueued;
    private string _reason="desktop";
    internal event EventHandler? PresentationChanged;

    internal DockLayerService(Window dock)
    {
        _dock=dock;
        _locationCallback=OnLocationChanged;
        _locationHook=SetWinEventHook(0x800B,0x800B,0,_locationCallback,0,0,0);
    }

    internal bool Update()
    {
        if(_disposed) return false;
        _dock.Dispatcher.VerifyAccess();
        var handle=new WindowInteropHelper(_dock).Handle;
        if(handle==0) return false;
        _foreground=DesktopNative.ForegroundForDesktopPolicy();
        bool allow=ShouldOverlay(_foreground,handle);
        if(_dock.Topmost!=allow) _dock.Topmost=allow;
        GetWindowThreadProcessId(_foreground,out uint foregroundProcess);
        if(allow && ((GetWindowLong(handle,-20)&8)==0 || foregroundProcess!=(uint)Environment.ProcessId && IsAbove(_foreground,handle)))
            SetWindowPos(handle,-1,0,0,0,0,0x0013);
        if(!allow && _dock.IsVisible) DesktopNative.PlaceOnDesktop(_dock);
        _allowOverlay=allow;
        return allow;
    }
    private static bool IsAbove(nint foreground,nint dock)
    {
        if(foreground==0 || foreground==dock)return false;
        for(nint window=GetTopWindow(0),count=0;window!=0 && count<4096;window=GetWindow(window,2),count++)
        { if(window==dock)return false; if(window==foreground)return true; }
        return false;
    }

    private bool ShouldOverlay(nint foreground,nint dockHandle)
    {
        if(foreground==0 || !IsWindowVisible(foreground) || IsIconic(foreground))
            return _allowOverlay;
        GetWindowThreadProcessId(foreground,out uint process);
        // Opening our settings or a control panel must not lift Dock over the
        // maximized application from which that panel was opened.
        if(process==(uint)Environment.ProcessId) return _allowOverlay;

        nint application=GetAncestor(foreground,2);
        if(application==0) application=foreground;
        for(int depth=0;depth<8;depth++)
        {
            var owner=GetWindow(application,4);
            if(owner==0 || !IsWindowVisible(owner)) break;
            application=owner;
        }
        var className=new StringBuilder(128);
        GetClassName(application,className,className.Capacity);
        if(className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")return _allowOverlay;
        if(className.ToString() is "Progman" or "WorkerW")
        { _reason="desktop"; return true; }
        if(DwmGetWindowAttribute(application,14,out int cloaked,sizeof(int))==0 && cloaked!=0)
            return _allowOverlay;

        var monitor=DesktopNative.MonitorRect(_dock);
        bool coversMonitor=GetWindowRect(application,out var bounds)
            && bounds.Left<=monitor.Left+1 && bounds.Top<=monitor.Top+1
            && bounds.Right>=monitor.Right-1 && bounds.Bottom>=monitor.Bottom-1;
        bool sameMonitor=MonitorFromWindow(application,2)==MonitorFromWindow(dockHandle,2);
        bool covered=coversMonitor || sameMonitor && IsZoomed(application);
        _reason=covered?"maximized-or-fullscreen":"restored-window";
        return !covered;
    }

    private void OnLocationChanged(nint hook,uint eventType,nint window,int objectId,int childId,uint thread,uint time)
    {
        if(_disposed || objectId!=0 || childId!=0 || _updateQueued || _dock.Dispatcher.HasShutdownStarted) return;
        var foreground=DesktopNative.GetForegroundWindow();
        if(window!=foreground && window!=GetAncestor(foreground,2)) return;
        GetWindowThreadProcessId(window,out uint process);
        if(process==(uint)Environment.ProcessId) return;
        _updateQueued=true;
        _dock.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=> {
            _updateQueued=false;
            if(!_disposed) PresentationChanged?.Invoke(this,EventArgs.Empty);
        }));
    }

    internal object Inspect()
    {
        var handle=new WindowInteropHelper(_dock).Handle;
        return new { overlay=_allowOverlay,reason=_reason,foreground=_foreground.ToInt64(),
            nativeTopmost=handle!=0 && (GetWindowLong(handle,-20)&8)!=0,
            locationHook=_locationHook!=0 };
    }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true;
        if(_locationHook!=0) DesktopNative.UnhookWinEvent(_locationHook);
        _locationHook=0;
    }

    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint window,StringBuilder name,int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out DesktopNative.Rect bounds);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window,uint attribute,out int value,int size);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint minimum,uint maximum,nint module,DesktopNative.WinEventCallback callback,uint process,uint thread,uint flags);
}
