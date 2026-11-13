using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassDock.Shell;

public enum TrayClick { Primary,Secondary,Double,Middle,KeyboardPrimary }

public sealed record HostedTrayIcon(string Key,string OwnerPath,string Tooltip,nint WindowHandle,uint IconId,
    Guid Guid,uint CallbackMessage,uint Version,bool Hidden,BitmapSource? Image,bool HasVisualRegistration=false);

/// <summary>
/// Receives real Shell_NotifyIcon registrations in a hidden Shell_TrayWnd, retaining the
/// application's callback message and version. Explorer also receives the original packets,
/// so its notification cache, balloons and recovery remain available. No injected code or
/// remote-process memory reads are used. Architecture and wire layout were reviewed against
/// cairoshell/ManagedShell's Apache-2.0 TrayService/NotifyIcon implementation.
/// </summary>
public sealed class TrayHostService : IDisposable
{
    private const string ClassName="Shell_TrayWnd",NotifyClass="TrayNotifyWnd";
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string,Entry> _entries=new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<object> _packetTrace=new();
    private readonly DispatcherTimer _monitor,_changeTimer,_warmupTimer;
    private readonly WindowProcedure _windowProcedure;
    private readonly nint _instance=GetModuleHandle(null);
    private readonly uint _taskbarCreated;
    private ReplayState? _replay;
    private int _replaySequence;
    private nint _window,_notifyWindow,_explorer;
    private uint _explorerProcess;
    private bool _disposed,_started,_changed;
    private long _received,_adds,_modifies,_deletes,_versions,_rejected,_forwardTimeouts;
    private long _lastCleanup;
    private Rect _defaultPlacement=new(SystemParameters.PrimaryScreenWidth-80,100,32,32);
    public event EventHandler? Changed;
    public nint Handle=>_window;
    public bool IsRunning=>_started && !_disposed && _window!=0;
    public bool IsPreferredHost=>IsRunning && FindWindow(ClassName,null)==_window;
    public string? LastError { get; private set; }
    public object Diagnostics=>new { running=IsRunning,preferred=IsPreferredHost,received=_received,adds=_adds,modifies=_modifies,deletes=_deletes,versions=_versions,rejected=_rejected,forwardTimeouts=_forwardTimeouts,icons=_entries.Count,visibleIcons=_entries.Values.Count(entry=>(entry.State&1)==0),completeVisibleIcons=_entries.Values.Count(entry=>(entry.State&1)==0 && entry.Image is not null && entry.CallbackMessage>=0x400),incompleteIcons=_entries.Values.Count(entry=>entry.Image is null || entry.CallbackMessage<0x400),error=LastError,replay=_replay?.Inspect(),packets=_packetTrace.ToArray() };
    public IReadOnlyList<HostedTrayIcon> Snapshot
    {
        get
        {
            _dispatcher.VerifyAccess();
            ExpireTransientFrames();
            return _entries.Values.OrderBy(entry=>entry.Sequence).Select(entry=>entry.ToIcon()).ToArray();
        }
    }
    internal bool ReceiveForProbe(uint operation,TrayProtocol.WireIcon data) {_dispatcher.VerifyAccess(); return Receive(operation,data);}

    public TrayHostService(Dispatcher? dispatcher=null)
    {
        _dispatcher=dispatcher??Dispatcher.CurrentDispatcher;
        _windowProcedure=WndProc;
        _taskbarCreated=RegisterWindowMessage("TaskbarCreated");
        _monitor=new DispatcherTimer(DispatcherPriority.Background,_dispatcher) { Interval=TimeSpan.FromMilliseconds(100) };
        _monitor.Tick+=(_,_)=>Monitor();
        _changeTimer=new DispatcherTimer(DispatcherPriority.Background,_dispatcher) { Interval=TimeSpan.FromMilliseconds(16) };
        _changeTimer.Tick+=(_,_)=> { _changeTimer.Stop(); if(!_changed) return; _changed=false; Changed?.Invoke(this,EventArgs.Empty); };
        _warmupTimer=new DispatcherTimer(DispatcherPriority.Background,_dispatcher) { Interval=TimeSpan.FromSeconds(2) };
        _warmupTimer.Tick+=(_,_)=> { _warmupTimer.Stop(); RequestRefresh(); };
    }

    public void Start()
    {
        _dispatcher.VerifyAccess(); ObjectDisposedException.ThrowIf(_disposed,this);
        if(_started) return;
        TrayProtocol.ValidateLayouts();
        RegisterClassName(ClassName); RegisterClassName(NotifyClass);
        _window=CreateWindowEx(0x08000088,ClassName,"",unchecked((int)0x86000000),0,0,1,1,0,0,_instance,0);
        if(_window==0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法创建托盘接收窗口");
        _notifyWindow=CreateWindowEx(0,NotifyClass,"",0x46000000,0,0,1,1,_window,0,_instance,0);
        if(_notifyWindow==0) { DestroyWindow(_window); _window=0; throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法创建通知区域"); }
        _started=true;
        PreferHost(); _monitor.Start();
        RequestRefresh();
        // Explorer/taskbar hiding may finish after startup. Request one complete replay
        // after that settles, then retain normal event-driven updates without replaying.
        _warmupTimer.Start();
    }

    public void UpdateHostBounds(Rect screenBounds)
    {
        _dispatcher.VerifyAccess();
        if(screenBounds.IsEmpty || screenBounds.Width<1 || screenBounds.Height<1) return;
        _defaultPlacement=screenBounds;
        if(IsRunning) SetWindowPos(_window,0,(int)screenBounds.X,(int)screenBounds.Y,(int)screenBounds.Width,(int)screenBounds.Height,0x0014);
    }

    public void UpdatePlacement(string key,Rect physicalScreenBounds)
    {
        _dispatcher.VerifyAccess();
        if(!physicalScreenBounds.IsEmpty && _entries.TryGetValue(key,out var entry)) entry.Placement=physicalScreenBounds;
    }

    public bool Invoke(string key,TrayClick click,Point screenAnchor)
    {
        _dispatcher.VerifyAccess();
        if(!_entries.TryGetValue(key,out var entry) || !IsValid(entry) || entry.CallbackMessage<0x400) return false;
        AllowSetForegroundWindow(entry.Process);
        entry.Placement=new Rect(screenAnchor.X-12,screenAnchor.Y-12,24,24);
        bool Send(uint message)=>SendCallback(entry,message,screenAnchor);
        return click switch
        {
            TrayClick.Primary=>Send(0x0201)&Send(0x0202)&(entry.Version<3 || Send(0x400)),
            TrayClick.Double=>Send(0x0203)&Send(0x0202)&(entry.Version<3 || Send(0x400)),
            TrayClick.Secondary=>Send(0x0204)&Send(0x0205)&(entry.Version<3 || Send(0x007B)),
            TrayClick.Middle=>Send(0x0207)&Send(0x0208),
            TrayClick.KeyboardPrimary=>Send(entry.Version>=3?0x401u:0x0202u),
            _=>false
        };
    }

    public bool Hover(string key,bool entered,Point screenAnchor)
    {
        _dispatcher.VerifyAccess();
        if(!_entries.TryGetValue(key,out var entry) || !IsValid(entry) || entry.CallbackMessage<0x400) return false;
        bool sent=SendCallback(entry,entered?0x02A1u:0x02A3u,screenAnchor);
        if(entry.Version>3) sent&=SendCallback(entry,entered?0x406u:0x407u,screenAnchor);
        return sent;
    }

    public void RequestRefresh()
    {
        _dispatcher.VerifyAccess();
        if(!IsRunning) return;
        PreferHost();
        _replay?.Cancel();
        GetWindowThreadProcessId(GetShellWindow(),out uint shellProcess);
        var targets=new List<ReplayTarget>();
        EnumWindows((window,_)=>
        {
            uint thread=GetWindowThreadProcessId(window,out uint process);
            // Notifying Explorer during collection can raise its taskbar before another
            // application re-registers. Its original notification cache is updated by Forward.
            if(thread!=0 && process!=0 && process!=Environment.ProcessId && process!=shellProcess)
                targets.Add(new ReplayTarget(window,thread,process));
            return targets.Count<1024;
        },0);
        var replay=new ReplayState(++_replaySequence,targets.Count);
        _replay=replay;
        _=Task.Run(()=>ReplayTaskbarCreatedAsync(replay,targets));
    }

    private async Task ReplayTaskbarCreatedAsync(ReplayState replay,IReadOnlyList<ReplayTarget> targets)
    {
        var token=replay.Cancellation.Token;
        long started=Environment.TickCount64;
        var unavailableThreads=new HashSet<uint>();
        try
        {
            foreach(var target in targets)
            {
                token.ThrowIfCancellationRequested();
                if(Environment.TickCount64-started>=6000) { replay.Result="time-limit"; break; }
                if(unavailableThreads.Contains(target.Thread)) { replay.Skipped++; continue; }
                // The sender must not occupy the UI thread while an application handles
                // TaskbarCreated and synchronously calls Shell_NotifyIcon back into us.
                bool ready=await _dispatcher.InvokeAsync(()=>
                {
                    if(!IsRunning || !ReferenceEquals(_replay,replay) || token.IsCancellationRequested) return false;
                    if(!IsWindow(target.Window) || GetWindowThreadProcessId(target.Window,out uint process)!=target.Thread || process!=target.Process) return false;
                    PreferHost();
                    return true;
                },DispatcherPriority.Send,token).Task.ConfigureAwait(false);
                if(!ready) { replay.Skipped++; continue; }
                token.ThrowIfCancellationRequested();
                replay.Attempted++;
                bool completed=SendMessageTimeout(target.Window,_taskbarCreated,0,0,0x0022,120,out _)!=0;
                if(completed) replay.Completed++;
                else
                {
                    replay.NotCompleted++;
                    replay.LastSendError=Marshal.GetLastWin32Error();
                    unavailableThreads.Add(target.Thread);
                }
            }
            if(replay.Result=="running") replay.Result=replay.Targets>=1024?"target-limit":"completed";
        }
        catch(OperationCanceledException) { replay.Result="cancelled"; }
        catch(Exception exception)
        {
            replay.Result="failed";
            replay.Error=exception.Message;
            Program.Log("Tray registration replay: "+exception);
        }
        finally
        {
            replay.ElapsedMilliseconds=Environment.TickCount64-started;
            replay.Complete();
        }
    }

    private static bool SendCallback(Entry entry,uint message,Point screenAnchor)
    {
        nuint wParam=entry.Version>3?unchecked((nuint)TrayProtocol.PackedPoint(screenAnchor)):entry.Id;
        nint lParam=unchecked((nint)(long)(message|(entry.Version>3?(entry.Id&0xffff)<<16:0)));
        return PostMessage(entry.Window,entry.CallbackMessage,wParam,lParam);
    }

    private void RegisterClassName(string name)
    {
        var wc=new WindowClass { Size=(uint)Marshal.SizeOf<WindowClass>(),Style=8,Procedure=_windowProcedure,Instance=_instance,ClassName=name };
        if(RegisterClassEx(ref wc)==0 && Marshal.GetLastWin32Error()!=1410)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"无法注册通知区域窗口");
    }

    private void Monitor()
    {
        if(!IsRunning) return;
        PreferHost();
        ExpireTransientFrames();
        if(Environment.TickCount64-_lastCleanup<5000) return;
        _lastCleanup=Environment.TickCount64;
        foreach(var key in _entries.Where(pair=>!IsValid(pair.Value)).Select(pair=>pair.Key).ToArray()) { _entries.Remove(key); MarkChanged(); }
    }
    private void ExpireTransientFrames()
    {
        if(!_entries.Values.Any(entry=>entry.DeleteAfter>0 || entry.HiddenFrameUntil>0))return;
        long now=Environment.TickCount64;
        foreach(var pair in _entries.ToArray())
        {
            var entry=pair.Value;
            if(entry.DeleteAfter>0 && (now>=entry.DeleteAfter || !IsValid(entry))) {_entries.Remove(pair.Key); MarkChanged();}
            else if(entry.HiddenFrameUntil>0 && now>=entry.HiddenFrameUntil) {entry.HiddenFrameUntil=0; MarkChanged();}
        }
    }

    private void PreferHost()
    {
        if(_window==0 || FindWindow(ClassName,null)==_window) return;
        SetWindowPos(_window,-1,0,0,0,0,0x0013); // Hidden topmost; no SHOWWINDOW or activation.
    }

    private nint WndProc(nint window,uint message,nuint wParam,nint lParam)
    {
        try
        {
            if(message==TrayProtocol.WmCopyData)
            {
                _received++;
                if(TrayProtocol.TryReadIdentifier(lParam,out var identifier))
                {
                    var entry=Find(identifier.Guid,TrayProtocol.Handle(identifier.Window),identifier.Id);
                    if(entry is not null)
                    {
                        var placement=entry.Placement.IsEmpty?_defaultPlacement:entry.Placement;
                        // Shell_NotifyIconGetRect adds the second packed result to the first.
                        // Message 2 therefore returns extent, not an absolute bottom-right.
                        return TrayProtocol.PackedPoint(identifier.Message==1?placement.TopLeft:new Point(placement.Width,placement.Height));
                    }
                }
                bool recognized=TrayProtocol.TryRead(lParam,out var operation,out var icon);
                bool accepted=recognized && Receive(operation,icon);
                var forwarded=Forward(message,wParam,lParam,out bool forwardedOk);
                if(_packetTrace.Count<32 && lParam!=0)
                {
                    var packet=Marshal.PtrToStructure<TrayProtocol.CopyData>(lParam);
                    _packetTrace.Enqueue(new { kind=(ulong)packet.Kind,length=packet.Length,recognized,operation,id=icon.Id,window=icon.Window,accepted,preferredAfterForward=IsPreferredHost });
                }
                return recognized?(accepted?1:0):forwardedOk?forwarded:0;
            }
            if(message==0x0047 && _window!=0)
            {
                // Explorer/appbar clients can request showing their discovered tray window.
                // This is a message-only visual surface and must remain invisible.
                int style=GetWindowLong(_window,-16);
                if((style&0x10000000)!=0) SetWindowLong(_window,-16,style&~0x10000000);
            }
            if(message==_taskbarCreated)
            {
                if(_started) PreferHost();
                return 0; // Never rebroadcast a received creation message.
            }
            if(message==0x001C || message==0x0111 || message>=0x400)
            {
                // Windows uses this fire-and-forget shell command to avoid a send cycle.
                if(message==0x400+372) { var explorer=FindExplorer(); if(explorer!=0) PostMessage(explorer,message,wParam,lParam); return 0; }
                var forwarded=Forward(message,wParam,lParam,out bool forwardedOk);
                if(forwardedOk) return forwarded;
            }
        }
        catch(Exception exception) { LastError=exception.Message; Program.Log("Tray host: "+exception); }
        return DefWindowProc(window,message,wParam,lParam);
    }

    private readonly ExplorerForwardingGate _forwardingGate=new();

    private nint Forward(uint message,nuint wParam,nint lParam,out bool completed)
    {
        completed=false;
        var explorer=FindExplorer();
        if(explorer==0 || explorer==_window || explorer==_notifyWindow) return 0;
        if(!_forwardingGate.CanSend(Environment.TickCount64)) return 0;
        completed=_forwardingGate.TryForward(explorer,message,wParam,lParam,out var result);
        if(!completed) _forwardTimeouts++;
        // Explorer can raise its Shell_TrayWnd while processing an icon/appbar packet.
        // Reclaim discovery before returning to shell32, otherwise a burst of subsequent
        // Shell_NotifyIcon calls can go straight to Explorer until the monitor's next tick.
        if(IsRunning) PreferHost();
        return unchecked((nint)result);
    }

    private nint FindExplorer()
    {
        if(_explorer!=0 && IsWindow(_explorer) && GetWindowThreadProcessId(_explorer,out uint process)!=0 && process==_explorerProcess)
        {
            GetWindowThreadProcessId(GetShellWindow(),out uint shellProcess);
            if(shellProcess==process) return _explorer;
        }
        _explorer=TaskbarLease.FindExplorerTaskbar();
        GetWindowThreadProcessId(_explorer,out _explorerProcess);
        return _explorer;
    }

    private Entry? Find(Guid guid,nint window,uint id)=>guid!=Guid.Empty
        ?_entries.Values.FirstOrDefault(entry=>entry.Guid==guid)
        :_entries.Values.FirstOrDefault(entry=>entry.Window==window && entry.Id==id);

    private bool Receive(uint operation,TrayProtocol.WireIcon data)
    {
        nint owner=TrayProtocol.Handle(data.Window);
        Guid guid=(data.Flags&TrayProtocol.GuidFlag)!=0?data.Guid:Guid.Empty;
        var entry=Find(guid,owner,data.Id);
        if(operation==TrayProtocol.Delete)
        {
            _deletes++;
            if(entry is null) return false;
            // Some notification implementations flash by deleting and re-adding.
            // Retain an empty slot briefly only for a previously real, live owner.
            if(entry.HasVisualRegistration && IsValid(entry)) {entry.DeleteAfter=Environment.TickCount64+800; entry.Image=null;}
            else _entries.Remove(entry.Key);
            MarkChanged(); return true;
        }
        if(operation==TrayProtocol.SetVersion)
        {
            _versions++;
            if(entry is null || data.Version>4) return false;
            entry.Version=data.Version; MarkChanged(); return true;
        }
        if(operation==TrayProtocol.SetFocus) return entry is not null;
        if(operation is not (TrayProtocol.Add or TrayProtocol.Modify)) return false;
        if(operation==TrayProtocol.Add) _adds++; else _modifies++;
        if(entry is not null && owner!=0 && !IsValid(entry)) { _entries.Remove(entry.Key); entry=null; }
        bool existed=entry is not null && entry.DeleteAfter==0;
        if(entry is null)
        {
            if(owner==0 || !IsWindow(owner) || _entries.Count>=512) { _rejected++; return false; }
            uint thread=GetWindowThreadProcessId(owner,out uint process);
            if(thread==0 || process==0) { _rejected++; return false; }
            string path="";
            try { using var application=Process.GetProcessById((int)process); path=application.MainModule?.FileName??""; } catch { }
            string key=guid!=Guid.Empty?guid.ToString("D"):$"{owner.ToInt64():X}:{data.Id}";
            entry=new Entry { Key=key,Window=owner,Id=data.Id,Guid=guid,Process=process,Thread=thread,OwnerPath=path,Sequence=Environment.TickCount64,Placement=_defaultPlacement };
            _entries[key]=entry;
        }
        if(operation==TrayProtocol.Add) entry.Version=0;
        entry.DeleteAfter=0;
        if(owner!=0)
        {
            uint thread=GetWindowThreadProcessId(owner,out uint process);
            bool ownerChanged=entry.Window!=owner || entry.Process!=process;
            entry.Window=owner; entry.Id=data.Id; entry.Thread=thread; entry.Process=process;
            if(ownerChanged)
            {
                try { using var application=Process.GetProcessById((int)process); entry.OwnerPath=application.MainModule?.FileName??""; }
                catch { entry.OwnerPath=""; }
            }
        }
        if((data.Flags&TrayProtocol.Message)!=0) entry.CallbackMessage=data.CallbackMessage;
        if((data.Flags&TrayProtocol.Tip)!=0) entry.Tooltip=data.Tip??"";
        if((data.Flags&TrayProtocol.State)!=0)
        {
            bool wasHidden=(entry.State&1)!=0;
            entry.State=(entry.State&~data.StateMask)|(data.State&data.StateMask);
            if((entry.State&1)==0)entry.HiddenFrameUntil=0;
            else if(!wasHidden && entry.HasVisualRegistration)entry.HiddenFrameUntil=Environment.TickCount64+800;
        }
        if((data.Flags&TrayProtocol.Icon)!=0)
        {
            if(data.IconHandle==0) entry.Image=null;
            else if(CopyBitmap(TrayProtocol.Handle(data.IconHandle)) is { } bitmap) {entry.Image=bitmap; entry.HasVisualRegistration=true;}
        }
        MarkChanged();
        // Missing MODIFY registrations are retained as partial entries, but returning failure
        // allows well-behaved apps to resend their complete NIM_ADD metadata.
        return operation==TrayProtocol.Add || existed;
    }

    private static BitmapSource? CopyBitmap(nint source)
    {
        nint copied=CopyIcon(source); if(copied==0) return null;
        try
        {
            // Retain the supplied pixels. Enlarging 16/24px icons to 32 here and
            // shrinking them again in the panel produces an avoidable second filter.
            var bitmap=Imaging.CreateBitmapSourceFromHIcon(copied,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze(); return bitmap;
        }
        catch(ArgumentException) { return null; }
        finally { DestroyIcon(copied); }
    }

    private static bool IsValid(Entry entry)=>IsWindow(entry.Window) && GetWindowThreadProcessId(entry.Window,out uint process)==entry.Thread && process==entry.Process;
    private void MarkChanged() { _changed=true; if(!_changeTimer.IsEnabled) _changeTimer.Start(); }
    private void BroadcastTaskbarCreated() { if(_taskbarCreated!=0) SendNotifyMessage((nint)0xffff,_taskbarCreated,0,0); }
    public static void ValidateLayouts()=>TrayProtocol.ValidateLayouts();

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if(_disposed) return; bool wasStarted=_started; _disposed=true; _started=false;
        _replay?.Cancel();
        _monitor.Stop(); _changeTimer.Stop(); _warmupTimer.Stop();
        if(_notifyWindow!=0) { DestroyWindow(_notifyWindow); _notifyWindow=0; }
        if(_window!=0) { DestroyWindow(_window); _window=0; }
        UnregisterClass(NotifyClass,_instance); UnregisterClass(ClassName,_instance);
        _entries.Clear();
        if(wasStarted)BroadcastTaskbarCreated(); // Explorer now owns the first Shell_TrayWnd again.
        GC.KeepAlive(_windowProcedure);
    }

    private sealed class Entry
    {
        internal string Key="",OwnerPath="",Tooltip="";
        internal nint Window;
        internal uint Id,CallbackMessage,Version,State,Process,Thread;
        internal Guid Guid;
        internal long Sequence;
        internal long DeleteAfter,HiddenFrameUntil;
        internal Rect Placement;
        internal BitmapSource? Image;
        internal bool HasVisualRegistration;
        internal HostedTrayIcon ToIcon()=>new(Key,OwnerPath,Tooltip,Window,Id,Guid,CallbackMessage,Version,(State&1)!=0 && HiddenFrameUntil==0,HiddenFrameUntil>0?null:Image,HasVisualRegistration);
    }
    private readonly record struct ReplayTarget(nint Window,uint Thread,uint Process);
    private sealed class ReplayState
    {
        private readonly object _sync=new();
        private bool _complete;
        internal readonly CancellationTokenSource Cancellation=new();
        internal readonly int Sequence,Targets;
        internal int Attempted,Completed,NotCompleted,Skipped,LastSendError;
        internal long ElapsedMilliseconds;
        internal string Result="running";
        internal string? Error;
        internal ReplayState(int sequence,int targets) { Sequence=sequence; Targets=targets; }
        internal void Cancel() { lock(_sync) if(!_complete) Cancellation.Cancel(); }
        internal void Complete() { lock(_sync) { _complete=true; Cancellation.Dispose(); } }
        internal object Inspect()=>new { sequence=Sequence,active=!Volatile.Read(ref _complete),targets=Targets,targetLimitReached=Targets>=1024,attempted=Volatile.Read(ref Attempted),completed=Volatile.Read(ref Completed),notCompleted=Volatile.Read(ref NotCompleted),skipped=Volatile.Read(ref Skipped),lastSendError=Volatile.Read(ref LastSendError),elapsedMilliseconds=Interlocked.Read(ref ElapsedMilliseconds),result=Result,error=Error };
    }
    private delegate nint WindowProcedure(nint window,uint message,nuint wParam,nint lParam);
    private delegate bool EnumWindowsProcedure(nint window,nint parameter);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size,Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra,WindowExtra;
        internal nint Instance,Icon,Cursor,Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool UnregisterClass(string className,nint instance);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint CreateWindowEx(int extendedStyle,string className,string? title,int style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint DefWindowProc(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindow(string className,string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProcedure callback,nint parameter);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window,nint insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool PostMessage(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern nint SendMessageTimeout(nint window,uint message,nuint wParam,nint lParam,uint flags,uint timeout,out nuint result);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SendNotifyMessage(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint window,int index,int value);
    [DllImport("user32.dll")] private static extern nint CopyIcon(nint icon);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
