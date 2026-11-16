using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace GlassDock.Shell;

/// <summary>Holds our tray surfaces while a verified notification owner's native menu is open.</summary>
internal sealed class TrayMenuSession : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly WinEventProc _callback;
    private readonly HashSet<nint> _menus=[];
    private readonly HashSet<uint> _threads=[];
    private readonly HashSet<nint> _beforeVisible=[];
    private nint _hook;
    private uint _process;
    private long _started,_lastMenuAt;
    private bool _seen,_disposed;
    private int _generation;
    internal bool Active { get; private set; }
    internal event EventHandler? Changed;
    internal TrayMenuSession(Dispatcher dispatcher)
    {
        _dispatcher=dispatcher; _callback=OnMenuEvent;
        _timer=new DispatcherTimer(DispatcherPriority.Input,dispatcher) { Interval=TimeSpan.FromMilliseconds(60) };
        _timer.Tick+=(_,_)=>Poll();
    }
    internal void Begin(nint owner)
    {
        Stop(false); if(_disposed) return;
        uint thread=GetWindowThreadProcessId(owner,out _process);
        if(thread==0 || _process==0) return;
        EnumWindows((window,_)=> { GetWindowThreadProcessId(window,out uint process); if(process==_process && IsWindowVisible(window))_beforeVisible.Add(window); return true; },0);
        _threads.Add(thread); _started=_lastMenuAt=Environment.TickCount64; _seen=false; Active=true;
        _hook=SetWinEventHook(4,7,0,_callback,_process,0,0);
        _timer.Start(); Changed?.Invoke(this,EventArgs.Empty);
    }
    private void OnMenuEvent(nint hook,uint type,nint window,int objectId,int childId,uint thread,uint time)
    {
        if(!Active || _disposed || _dispatcher.HasShutdownStarted) return;
        int generation=_generation;
        _dispatcher.BeginInvoke(new Action(()=> {
            if(!Active || generation!=_generation) return;
            _threads.Add(thread);
            if(type is 4 or 6) { if(type==6 && window!=0)_menus.Add(window); _seen=true; _lastMenuAt=Environment.TickCount64; }
            else if(type is 5 or 7) { if(type==5)_menus.Clear(); else _menus.Remove(window); _lastMenuAt=Environment.TickCount64; }
        }),DispatcherPriority.Input);
    }
    private void Poll()
    {
        if(!Active) return;
        bool visible=false;
        EnumWindows((window,_)=> {
            if(!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window,out uint process); if(process!=_process) return true;
            var name=new StringBuilder(64); GetClassName(window,name,name.Capacity);
            string type=name.ToString();
            bool custom=!_beforeVisible.Contains(window) && (GetWindowLong(window,-16)&unchecked((int)0x80000000))!=0
                && !type.Contains("Tooltip",StringComparison.OrdinalIgnoreCase)
                && (type.Contains("Popup",StringComparison.OrdinalIgnoreCase) || type.Contains("Menu",StringComparison.OrdinalIgnoreCase)
                    || (GetWindowLong(window,-20)&0x80)!=0 && (type.StartsWith("HwndWrapper",StringComparison.Ordinal) || type=="Chrome_WidgetWin_2"));
            if(type=="#32768" || custom) { visible=true; _menus.Add(window); }
            return true;
        },0);
        foreach(var menu in _menus.ToArray())
        {
            GetWindowThreadProcessId(menu,out uint process);
            if(process!=_process || !IsWindowVisible(menu)) _menus.Remove(menu); else visible=true;
        }
        foreach(uint thread in _threads)
        {
            var info=new GuiThreadInfo { Size=(uint)Marshal.SizeOf<GuiThreadInfo>() };
            if(GetGUIThreadInfo(thread,ref info) && (info.Flags&0x1C)!=0) visible=true;
        }
        long now=Environment.TickCount64;
        if(visible) { _seen=true; _lastMenuAt=now; return; }
        if((_seen && now-_lastMenuAt>=120) || (!_seen && now-_started>=1000)) Cancel();
    }
    internal void Cancel()=>Stop(true);
    private void Stop(bool notify)
    {
        bool changed=Active; Active=false; _timer.Stop(); _generation++;
        if(_hook!=0) UnhookWinEvent(_hook); _hook=0; _menus.Clear(); _threads.Clear(); _beforeVisible.Clear();
        if(changed && notify) Changed?.Invoke(this,EventArgs.Empty);
    }
    public void Dispose() { if(_disposed)return; _disposed=true; Cancel(); GC.KeepAlive(_callback); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public uint Size,Flags; public nint Active,Focus,Capture,MenuOwner,MoveSize,Caret; public NativeRect CaretRect; }
    private delegate bool EnumProc(nint window,nint parameter);
    private delegate void WinEventProc(nint hook,uint type,nint window,int objectId,int childId,uint thread,uint time);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint window,StringBuilder text,int maximum);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min,uint max,nint module,WinEventProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
