using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassDock.Shell;

/// <summary>Background notification entry; Explorer owns its visible/overflow placement.</summary>
internal sealed class AppTrayIcon : IDisposable
{
    private const uint CallbackMessage=0x8000+230;
    // A new product identity avoids Explorer's GUID-to-old-executable-path cache.
    private static readonly Guid IconGuid=new("9E1476F7-A246-4A6F-B926-44A87F75F776");
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly nint _icon;
    private readonly Action _openSettings,_openControls,_quit;
    private readonly DispatcherTimer _retry=new() { Interval=TimeSpan.FromSeconds(3) };
    private ContextMenu? _menu;
    private bool _version4,_disposed;
    private int _retryCount;
    internal bool IsRegistered { get; private set; }
    internal nint OwnerWindow=>_source.Handle;
    internal static Guid Identity=>IconGuid;
    internal static void ValidateContracts()
    {
        int expected=IntPtr.Size==8?976:956;
        if(Marshal.SizeOf<NotifyIconData>()!=expected || Marshal.OffsetOf<NotifyIconData>(nameof(NotifyIconData.CallbackMessage)).ToInt32()!=(IntPtr.Size==8?24:16))
            throw new InvalidOperationException("Notification icon native layout is invalid");
    }

    internal object Inspect()
    {
        var identifier=new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=_source.Handle,Id=1,Guid=IconGuid };
        int result=ShellNotifyIconGetRect(ref identifier,out var rectangle);
        return new { Registered=IsRegistered,Version4=_version4,ShellRectangleAvailable=result>=0,ShellResult=result,
            OwnerWindow=_source.Handle.ToInt64(),IconGuid=IconGuid,Bounds=new { rectangle.Left,rectangle.Top,rectangle.Right,rectangle.Bottom } };
    }

    internal AppTrayIcon(Action openSettings,Action openControls,Action quit)
    {
        ValidateContracts();
        _openSettings=openSettings; _openControls=openControls; _quit=quit;
        _icon=CreateTrayIcon();
        var parameters=new HwndSourceParameters("uilicd notification icon")
        {
            WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000080,
            Width=0,Height=0,PositionX=-32000,PositionY=-32000
        };
        _source=new HwndSource(parameters); _source.AddHook(WindowProc);
        _taskbarCreated=RegisterWindowMessage("TaskbarCreated");
        _retry.Tick+=(_,_)=> { if(_disposed || ++_retryCount>10 || Register()) _retry.Stop(); };
        if(!Register()) _retry.Start();
    }

    private bool Register()
    {
        if(_disposed) return false;
        var data=Data(); data.Flags=0x1|0x2|0x4|0x20|0x80; // message, icon, tip, GUID, standard tooltip
        data.CallbackMessage=CallbackMessage; data.Icon=_icon; data.Tip=Branding.Name;
        IsRegistered=ShellNotifyIcon(0,ref data);
        if(!IsRegistered) return false;
        data.Flags=0x20; data.Version=4;
        _version4=ShellNotifyIcon(4,ref data);
        return true;
    }

    private nint WindowProc(nint hwnd,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(_taskbarCreated!=0 && (uint)message==_taskbarCreated)
        {
            IsRegistered=false; _retryCount=0;
            if(!Register()) _retry.Start();
            return 0;
        }
        if((uint)message!=CallbackMessage || _disposed) return 0;
        uint notification=_version4?(uint)((long)lParam&0xffff):unchecked((uint)(long)lParam);
        if(notification==0x007B || !_version4 && notification==0x0205)
            _source.Dispatcher.BeginInvoke(new Action(ShowMenu));
        else if(_version4 && notification is 0x0400 or 0x0401 || !_version4 && notification==0x0202)
            _source.Dispatcher.BeginInvoke(new Action(_openSettings));
        handled=true; return 0;
    }

    private void ShowMenu()
    {
        if(_disposed) return;
        _menu?.SetCurrentValue(ContextMenu.IsOpenProperty,false);
        var menu=new ContextMenu { Placement=PlacementMode.MousePoint };
        menu.Items.Add(Ui.Menu("控制中心",_openControls));
        menu.Items.Add(Ui.Menu("设置",_openSettings));
        menu.Items.Add(new Separator()); menu.Items.Add(Ui.Menu("退出",_quit));
        menu.Closed+=(_,_)=> { if(!_disposed) { var data=Data(); ShellNotifyIcon(3,ref data); } };
        _menu=menu;
        SetForegroundWindow(_source.Handle);
        menu.IsOpen=true;
    }

    private NotifyIconData Data()=>new()
    {
        Size=(uint)Marshal.SizeOf<NotifyIconData>(),Window=_source.Handle,Id=1,Flags=0x20,Guid=IconGuid,
        Tip="",Info="",InfoTitle=""
    };

    public void Dispose()
    {
        if(_disposed) return;
        _disposed=true; _retry.Stop();
        if(_menu is not null) _menu.IsOpen=false;
        var data=Data(); ShellNotifyIcon(2,ref data); IsRegistered=false;
        _source.RemoveHook(WindowProc); _source.Dispose(); DestroyIcon(_icon);
    }

    private static nint CreateTrayIcon()=>Branding.CreateNativeIcon(32);

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id,Flags,CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width,Height; public ushort Planes,BitCount; public uint Compression,ImageSize;
        public int XPixelsPerMeter,YPixelsPerMeter; public uint ColorsUsed,ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public uint XHotspot,YHotspot; public nint Mask,Color;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message,ref NotifyIconData data);
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconGetRect")] private static extern int ShellNotifyIconGetRect(ref IconIdentifier identifier,out NativeRect rectangle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll",SetLastError=true)] private static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern nint CreateBitmap(int width,int height,uint planes,uint bitsPerPixel,byte[] bits);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
}
