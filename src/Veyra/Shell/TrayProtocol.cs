using System.Runtime.InteropServices;
using System.Windows;

namespace GlassDock.Shell;

/// <summary>
/// Shell32's WM_COPYDATA wire protocol uses 32-bit HWND/HICON fields even in an x64
/// receiver. This is deliberately distinct from the public native NOTIFYICONDATA ABI.
/// Layout cross-checked against cairoshell/ManagedShell NativeMethods.Shell32.cs (Apache-2.0).
/// </summary>
internal static class TrayProtocol
{
    internal const uint Add=0,Modify=1,Delete=2,SetFocus=3,SetVersion=4;
    internal const uint Message=1,Icon=2,Tip=4,State=8,Info=16,GuidFlag=32;
    internal const uint WmCopyData=0x004A;

    [StructLayout(LayoutKind.Sequential)]
    internal struct CopyData { internal nuint Kind; internal int Length; internal nint Data; }

    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode,Pack=4)]
    internal struct WireIcon
    {
        internal uint Size,Window,Id,Flags,CallbackMessage,IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] internal string Tip;
        internal uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] internal string InfoText;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal uint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential,Pack=4)]
    internal struct WireIdentifier
    {
        internal uint Magic,Message,Size,Padding,Window,Id;
        internal Guid Guid;
    }

    internal static bool TryRead(nint copyData,out uint operation,out WireIcon icon)
    {
        operation=0; icon=default;
        if(copyData==0) return false;
        var data=Marshal.PtrToStructure<CopyData>(copyData);
        if(data.Kind!=1 || data.Data==0 || data.Length<32 || data.Length>65536) return false;
        operation=unchecked((uint)Marshal.ReadInt32(data.Data,4));
        if(operation>SetVersion) return false;
        int provided=data.Length-8;
        uint declared=unchecked((uint)Marshal.ReadInt32(data.Data,8));
        // Older shell32 versions can provide only a prefix. Zero-pad before marshaling;
        // never read beyond the actual WM_COPYDATA buffer supplied by Windows.
        if(declared<24 || declared>4096) return false;
        int length=Math.Min(provided,Marshal.SizeOf<WireIcon>());
        byte[] buffer=new byte[Marshal.SizeOf<WireIcon>()];
        Marshal.Copy(data.Data+8,buffer,0,length);
        var pin=GCHandle.Alloc(buffer,GCHandleType.Pinned);
        try { icon=Marshal.PtrToStructure<WireIcon>(pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
        return true;
    }

    internal static bool TryReadIdentifier(nint copyData,out WireIdentifier identifier)
    {
        identifier=default;
        if(copyData==0) return false;
        var data=Marshal.PtrToStructure<CopyData>(copyData);
        if(data.Kind!=3 || data.Data==0 || data.Length<Marshal.SizeOf<WireIdentifier>() || data.Length>4096) return false;
        identifier=Marshal.PtrToStructure<WireIdentifier>(data.Data);
        return identifier.Message is 1 or 2;
    }

    internal static nint Handle(uint value)=>unchecked((nint)(long)value);
    internal static nint PackedPoint(Point point)=>unchecked((nint)(int)(((uint)(ushort)(int)Math.Round(point.Y)<<16)|(ushort)(int)Math.Round(point.X)));

    public static void ValidateLayouts()
    {
        if(Marshal.SizeOf<WireIcon>()!=956 || Marshal.OffsetOf<WireIcon>(nameof(WireIcon.Guid)).ToInt32()!=936 ||
           Marshal.SizeOf<WireIdentifier>()!=40 || Marshal.SizeOf<CopyData>()!=(IntPtr.Size==8?24:12))
            throw new InvalidOperationException("Shell notification wire ABI mismatch.");
    }
}
