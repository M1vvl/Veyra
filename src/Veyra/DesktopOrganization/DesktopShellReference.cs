using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace GlassDock.DesktopOrganization;

/// <summary>Recycle Bin is a Shell namespace reference, never a filesystem directory.</summary>
internal static class DesktopShellReference
{
    internal const string RecycleBin="shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
    internal const string ShellIdList="Shell IDList Array",BoxPaths="GlassDock.BoxPaths";
    internal static bool IsRecycleBin(string? path)=>string.Equals(path,RecycleBin,StringComparison.OrdinalIgnoreCase)
        || string.Equals(path,"::{645FF040-5081-101B-9F08-00AA002F954E}",StringComparison.OrdinalIgnoreCase)
        || string.Equals(path,"shell:RecycleBinFolder",StringComparison.OrdinalIgnoreCase);
    internal static bool IsReference(string path)=>IsRecycleBin(path) || Path.IsPathFullyQualified(path);
    internal static ProcessStartInfo OpenInfo(string path)
    {
        if(!IsRecycleBin(path))return new ProcessStartInfo(path) { UseShellExecute=true };
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe")) { UseShellExecute=true };
        info.ArgumentList.Add(RecycleBin); return info;
    }
    internal static DesktopBoxItem CreateRecycleBin()
    {
        int initialized=CoInitializeEx(0,0); nint pidl=0,name=0;
        try
        {
            string caption="回收站";
            if(SHParseDisplayName(RecycleBin,0,out pidl,0,out _)>=0 && pidl!=0 && SHGetNameFromIDList(pidl,0,out name)>=0)
                caption=Marshal.PtrToStringUni(name)??caption;
            return new(Guid.NewGuid().ToString("N"),caption,RecycleBin,true);
        }
        finally { if(name!=0)Marshal.FreeCoTaskMem(name); if(pidl!=0)Marshal.FreeCoTaskMem(pidl); if(initialized>=0)CoUninitialize(); }
    }
    internal static string[] ReadPaths(IDataObject data)
    {
        try
        {
            if(data.GetData(BoxPaths) is string[] own)return own.Where(IsReference).Take(500).ToArray();
            if(data.GetData(DataFormats.FileDrop) is string[] files && files.Length>0)return files.Take(500).ToArray();
            return data.GetData(ShellIdList) switch { MemoryStream stream when stream.Length<=4*1024*1024=>ReadIdList(stream.ToArray()),byte[] bytes when bytes.Length<=4*1024*1024=>ReadIdList(bytes),_=>[] };
        }
        catch(Exception exception) when(exception is COMException or ArgumentException or IOException) { return []; }
    }
    internal static unsafe string[] ReadIdList(byte[] bytes)
    {
        if(bytes.Length<12)return [];
        uint count=BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if(count==0 || count>500 || 4L+(count+1L)*4>bytes.Length)return [];
        int header=4+(int)(count+1)*4;
        bool Valid(int offset)
        {
            if(offset<header || offset>=bytes.Length-1)return false;
            for(int at=offset;at<bytes.Length-1;)
            {
                int length=BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at));
                if(length==0)return true;
                if(length<2 || length>bytes.Length-at-2)return false;
                at+=length;
            }
            return false;
        }
        int parent=unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        if(!Valid(parent))return [];
        var paths=new List<string>();
        fixed(byte* buffer=bytes)
        {
            for(int index=0;index<count;index++)
            {
                int child=unchecked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8+index*4)));
                if(!Valid(child))continue;
                nint absolute=ILCombine((nint)(buffer+parent),(nint)(buffer+child)),name=0;
                try
                {
                    if(absolute==0 || SHGetNameFromIDList(absolute,0x80028000,out name)<0)continue;
                    string? path=Marshal.PtrToStringUni(name);
                    if(IsRecycleBin(path))paths.Add(RecycleBin);
                    else if(path is not null && Path.IsPathFullyQualified(path))paths.Add(path);
                }
                finally { if(name!=0)Marshal.FreeCoTaskMem(name); if(absolute!=0)Marshal.FreeCoTaskMem(absolute); }
            }
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static DataObject CreateDragData(DesktopBoxItem item)
    {
        if(!IsRecycleBin(item.Path))return new DataObject(DataFormats.FileDrop,new[]{item.Path});
        var data=new DataObject(); data.SetData(BoxPaths,new[]{RecycleBin});
        data.SetData(ShellIdList,new MemoryStream(CreateRecycleIdList())); return data;
    }
    internal static byte[] CreateRecycleIdList()
    {
        nint pidl=0;
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(RecycleBin,0,out pidl,0,out _));
            int length=checked((int)ILGetSize(pidl));
            if(length<2 || length>65536)throw new InvalidOperationException("Invalid Recycle Bin PIDL");
            var bytes=new byte[14+length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes,1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),12);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8),14);
            Marshal.Copy(pidl,bytes,14,length); return bytes;
        }
        finally { if(pidl!=0)Marshal.FreeCoTaskMem(pidl); }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(string name,nint context,out nint pidl,uint mask,out uint attributes);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(nint pidl,uint kind,out nint name);
    [DllImport("shell32.dll")] private static extern nint ILCombine(nint parent,nint child);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(nint pidl);
}
