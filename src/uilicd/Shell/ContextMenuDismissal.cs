using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassDock.Shell;

/// <summary>Closes our menus on outside input even when their desktop owner cannot activate.</summary>
internal sealed class ContextMenuDismissal : IDisposable
{
    private static ContextMenuDismissal? _current;
    private static bool _classHandlersRegistered;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<ContextMenu> _menus=[];
    private readonly HookCallback _mouseCallback,_keyboardCallback;
    private nint _mouseHook,_keyboardHook;
    private bool _disposed;
    internal int OpenMenuCount=>_menus.Count;
    internal int HookCount=>(_mouseHook!=0?1:0)+(_keyboardHook!=0?1:0);

    internal ContextMenuDismissal(Dispatcher dispatcher)
    {
        _dispatcher=dispatcher; _mouseCallback=MouseInput; _keyboardCallback=KeyboardInput;
        _current=this;
        if(_classHandlersRegistered) return;
        _classHandlersRegistered=true;
        EventManager.RegisterClassHandler(typeof(ContextMenu),ContextMenu.OpenedEvent,new RoutedEventHandler((sender,_)=>_current?.Opened((ContextMenu)sender)));
        EventManager.RegisterClassHandler(typeof(ContextMenu),ContextMenu.ClosedEvent,new RoutedEventHandler((sender,_)=>_current?.Closed((ContextMenu)sender)));
    }
    private void Opened(ContextMenu menu)
    {
        if(_disposed) return;
        menu.StaysOpen=false;
        foreach(var other in _menus.Where(other=>other!=menu).ToArray()) other.IsOpen=false;
        _menus.Add(menu);
        if(_mouseHook==0) _mouseHook=SetWindowsHookEx(14,_mouseCallback,GetModuleHandle(null),0);
        if(_keyboardHook==0) _keyboardHook=SetWindowsHookEx(13,_keyboardCallback,GetModuleHandle(null),0);
        if(_mouseHook==0 || _keyboardHook==0) Program.Log("Menu dismissal hook unavailable: "+Marshal.GetLastWin32Error());
    }
    private void Closed(ContextMenu menu)
    {
        _menus.Remove(menu);
        if(_menus.Count==0) Unhook();
    }
    private nint MouseInput(int code,nint message,nint data)
    {
        // Observe only. Never consume the click intended for another window, and
        // never run layout or synchronous window messages inside a low-level hook.
        if(code>=0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
        {
            var point=Marshal.PtrToStructure<NativePoint>(data);
            QueueDismiss(new Point(point.X,point.Y));
        }
        return CallNextHookEx(0,code,message,data);
    }
    private nint KeyboardInput(int code,nint message,nint data)
    {
        if(code>=0 && message is 0x0100 or 0x0104 && Marshal.ReadInt32(data)==0x1B)
        {
            // A focused WPF menu owns Escape (one level at a time for submenus).
            // The global hook only repairs an unfocused, no-activate owner menu.
            if(!_menus.Any(menu=>menu.IsKeyboardFocusWithin)) QueueDismiss(null);
        }
        return CallNextHookEx(0,code,message,data);
    }
    private void QueueDismiss(Point? outsidePoint)
    {
        if(_disposed || _dispatcher.HasShutdownStarted) return;
        var pending=_menus.ToArray();
        _dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(()=>
        {
            foreach(var menu in pending)
                if(menu.IsOpen && (outsidePoint is null || !Contains(menu,outsidePoint.Value))) menu.IsOpen=false;
        }));
    }
    private static bool Contains(ContextMenu menu,Point point)
    {
        if(ContainsVisual(menu,point)) return true;
        return ContainsSubmenu(menu,point);
    }
    private static bool ContainsSubmenu(ItemsControl owner,Point point)
    {
        foreach(var item in owner.Items)
        {
            var container=item as MenuItem??owner.ItemContainerGenerator.ContainerFromItem(item) as MenuItem;
            if(container?.IsSubmenuOpen!=true) continue;
            if(container.Template?.FindName("PART_Popup",container) is Popup { IsOpen:true,Child: { } child } && ContainsVisual(child,point)) return true;
            if(ContainsSubmenu(container,point)) return true;
        }
        return false;
    }
    private static bool ContainsVisual(System.Windows.Media.Visual visual,Point point)
    {
        if(PresentationSource.FromVisual(visual) is not HwndSource source || source.IsDisposed || !GetWindowRect(source.Handle,out var bounds)) return false;
        return point.X>=bounds.Left && point.X<bounds.Right && point.Y>=bounds.Top && point.Y<bounds.Bottom;
    }
    private void Unhook()
    {
        if(_mouseHook!=0) UnhookWindowsHookEx(_mouseHook);
        if(_keyboardHook!=0) UnhookWindowsHookEx(_keyboardHook);
        _mouseHook=_keyboardHook=0;
    }
    public void Dispose()
    {
        if(_disposed) return; _disposed=true;
        foreach(var menu in _menus.ToArray()) menu.IsOpen=false;
        _menus.Clear(); Unhook();
        if(_current==this) _current=null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    private delegate nint HookCallback(int code,nint message,nint data);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint SetWindowsHookEx(int type,HookCallback callback,nint module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook,int code,nint message,nint data);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out NativeRect bounds);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
}
