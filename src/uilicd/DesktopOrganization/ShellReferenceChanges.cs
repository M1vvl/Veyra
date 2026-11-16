using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace GlassDock.DesktopOrganization;

/// <summary>Shell rename notifications complement local directory watches without holding files open.</summary>
internal sealed class ShellReferenceChanges : IDisposable
{
    private const int Message=0x8000+317;
    private readonly HwndSource _source;
    private readonly Action<string,string?> _changed;
    private uint _registration;
    private nint _desktop;
    internal bool Registered=>_registration!=0;
    internal ShellReferenceChanges(Action<string,string?> changed)
    {
        _changed=changed;
        _source=new HwndSource(new HwndSourceParameters("GlassDock reference changes") { ParentWindow=new nint(-3),WindowStyle=0,Width=0,Height=0 });
        _source.AddHook(WndProc);
        if(SHGetSpecialFolderLocation(0,0,out _desktop)>=0)
        {
            var entry=new Entry { Pidl=_desktop,Recursive=true };
            // Shell-level only: no recursive system-wide filesystem interrupt watch.
            _registration=SHChangeNotifyRegister(_source.Handle,0x8002,0x1|0x4|0x10|0x1000|0x20000,Message,1,ref entry);
        }
    }
    private nint WndProc(nint window,int message,nint wParam,nint lParam,ref bool handled)
    {
        if(message!=Message)return 0;
        var locked=SHChangeNotification_Lock(wParam,unchecked((uint)lParam.ToInt64()),out var pidls,out int kind);
        if(locked==0)return 0;
        try
        {
            if(pidls==0)return 0;
            string? oldPath=PathOf(Marshal.ReadIntPtr(pidls));
            string? newPath=(kind&(0x1|0x20000))!=0?PathOf(Marshal.ReadIntPtr(pidls,IntPtr.Size)):null;
            if(oldPath is not null)_changed(oldPath,newPath);
        }
        finally { SHChangeNotification_Unlock(locked); }
        handled=true; return 0;
    }
    private static string? PathOf(nint pidl)
    {
        if(pidl==0)return null;
        var path=new StringBuilder(32768);
        return SHGetPathFromIDListEx(pidl,path,(uint)path.Capacity,0)?path.ToString():null;
    }
    public void Dispose()
    {
        if(_registration!=0) { SHChangeNotifyDeregister(_registration); _registration=0; }
        _source.RemoveHook(WndProc); _source.Dispose();
        if(_desktop!=0) { Marshal.FreeCoTaskMem(_desktop); _desktop=0; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Entry { public nint Pidl; [MarshalAs(UnmanagedType.Bool)] public bool Recursive; }
    [DllImport("shell32.dll")] private static extern int SHGetSpecialFolderLocation(nint owner,int folder,out nint pidl);
    [DllImport("shell32.dll")] private static extern uint SHChangeNotifyRegister(nint window,int sources,int events,uint message,int count,ref Entry entry);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotifyDeregister(uint registration);
    [DllImport("shell32.dll")] private static extern nint SHChangeNotification_Lock(nint change,uint process,out nint pidls,out int kind);
    [DllImport("shell32.dll")] private static extern bool SHChangeNotification_Unlock(nint locked);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern bool SHGetPathFromIDListEx(nint pidl,StringBuilder path,uint length,uint flags);
}
