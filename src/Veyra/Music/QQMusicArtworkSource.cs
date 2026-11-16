using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using GlassDock.Models;

namespace GlassDock.Music;

/// <summary>Read the title-bound artwork URL already supplied to QQ Music's SMTC bridge.</summary>
internal sealed class QQMusicArtworkSource : IDisposable
{
    private SafeProcessHandle? _process;
    private long _object;
    private int[] _fields=[];
    private long _retryAt,_checkAt;
    private string? _pictureDirectory;

    internal string? ReadUrl(MediaInfo media)
    {
        try
        {
            if(!EnsureProcess())return null;
            byte[] first=Read(_object+_fields[0],96);
            string[] values=new string[4];
            for(int i=0;i<values.Length;i++)
            {
                int length=BitConverter.ToInt32(first,i*24+16);
                int capacity=BitConverter.ToInt32(first,i*24+20);
                if(length<0 || length>4096 || capacity<length || capacity>1048576)return null;
                long address=capacity<8?_object+_fields[i]:BitConverter.ToUInt32(first,i*24);
                values[i]=Encoding.Unicode.GetString(Read(address,length*2));
            }
            if(!first.AsSpan().SequenceEqual(Read(_object+_fields[0],96)))return null;
            if(!Same(values[0],media.Title) || !Same(values[1],media.Artist))return null;
            if(!string.IsNullOrWhiteSpace(media.AlbumTitle) && !Same(values[2],media.AlbumTitle))return null;
            return string.IsNullOrWhiteSpace(values[3])?null:values[3];
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.ComponentModel.Win32Exception or BadImageFormatException)
        {
            Program.Log("QQ artwork source: "+ex.Message);
            _retryAt=Environment.TickCount64+2000;
            return null;
        }
    }

    internal async Task<byte[]?> ReadCachedAsync(Uri uri,CancellationToken token)
    {
        if(_pictureDirectory is null)
        {
            string settings=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tencent","QQMusic","WebkitCachePath.ini");
            if(!File.Exists(settings))return null;
            string? value=File.ReadLines(settings).FirstOrDefault(line=>line.StartsWith("Path=",StringComparison.OrdinalIgnoreCase));
            if(value is null || Path.GetDirectoryName(value[5..].Trim()) is not string root)return null;
            _pictureDirectory=Path.Combine(root,"QQMusicPicture");
        }
        string name=Path.GetFileName(uri.LocalPath);
        if(!name.StartsWith("T002R300x300M000",StringComparison.Ordinal) || name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return null;
        foreach(string size in new[] {"500x500","300x300","150x150"})
        {
            token.ThrowIfCancellationRequested();
            string path=Path.Combine(_pictureDirectory,name.Replace("300x300",size,StringComparison.Ordinal));
            var before=new FileInfo(path);
            if(!before.Exists || before.Length<=0 || before.Length>8*1024*1024)continue;
            long length=before.Length;
            DateTime modified=before.LastWriteTimeUtc;
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,
                16384,FileOptions.Asynchronous|FileOptions.SequentialScan);
            byte[] bytes=new byte[(int)length];
            await stream.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
            var after=new FileInfo(path);
            if(after.Exists && after.Length==length && after.LastWriteTimeUtc==modified)return bytes;
        }
        return null;
    }

    private static bool Same(string left,string right)=>string.Equals(
        string.Concat(left.Normalize(NormalizationForm.FormKC).Where(character=>!char.IsWhiteSpace(character))),
        string.Concat(right.Normalize(NormalizationForm.FormKC).Where(character=>!char.IsWhiteSpace(character))),
        StringComparison.OrdinalIgnoreCase);

    private bool EnsureProcess()
    {
        long now=Environment.TickCount64;
        if(_object!=0 && _process is {IsInvalid:false,IsClosed:false})
        {
            if(now<_checkAt)return true;
            _checkAt=now+1000;
            if(GetExitCodeProcess(_process,out uint code) && code==259)return true;
        }
        if(now<_retryAt)return false;
        _retryAt=now+2000;
        _object=0;
        var processes=Process.GetProcessesByName("QQMusic");
        try
        {
            foreach(var process in processes)
            {
                var module=process.Modules.Cast<ProcessModule>().FirstOrDefault(item=>
                    item.ModuleName.Equals("SMTCFeature.dll",StringComparison.OrdinalIgnoreCase));
                if(module is null)continue;
                var layout=Locate(module.FileName);
                if(layout is null)continue;
                _process?.Dispose();
                _process=OpenProcess(0x410,false,process.Id);
                if(_process.IsInvalid)return false;
                _object=module.BaseAddress.ToInt64()+layout.Value.Object;
                _fields=layout.Value.Fields;
                _checkAt=now+1000;
                return true;
            }
            return false;
        }
        finally {foreach(var process in processes)process.Dispose();}
    }

    private static (long Object,int[] Fields)? Locate(string path)
    {
        if(new FileInfo(path).Length>4*1024*1024)return null;
        byte[] bytes=File.ReadAllBytes(path);
        using var image=new PEReader(new MemoryStream(bytes));
        var headers=image.PEHeaders;
        if(headers.CoffHeader.Machine!=Machine.I386 || headers.PEHeader is null)return null;
        int Offset(long rva)
        {
            foreach(var section in headers.SectionHeaders)
                if(rva>=section.VirtualAddress && rva<(long)section.VirtualAddress+section.SizeOfRawData)
                    return checked(section.PointerToRawData+(int)(rva-section.VirtualAddress));
            throw new BadImageFormatException("QQ SMTC image address is outside its sections");
        }
        uint U32(int offset)=>BitConverter.ToUInt32(bytes,offset);
        int exports=Offset(headers.PEHeader.ExportTableDirectory.RelativeVirtualAddress);
        int names=Offset(U32(exports+32)),ordinals=Offset(U32(exports+36)),functions=Offset(U32(exports+28));
        long initialize=0;
        for(int i=0;i<U32(exports+24);i++)
        {
            int name=Offset(U32(names+i*4));
            int end=Array.IndexOf(bytes,(byte)0,name);
            if(end<name || Encoding.ASCII.GetString(bytes,name,end-name)!="InitializeSMTC")continue;
            initialize=U32(functions+BitConverter.ToUInt16(bytes,ordinals+i*2)*4);
            break;
        }
        if(initialize==0)return null;
        int entry=Offset(initialize);
        long objectRva=0;
        for(int i=0;i<48 && entry+i+6<bytes.Length;i++)
            if(bytes[entry+i]==0xb8 && bytes[entry+i+5]==0x5d && bytes[entry+i+6]==0xc3)
                objectRva=U32(entry+i+1)-(long)headers.PEHeader.ImageBase;
        if(objectRva<=0)return null;
        long table=U32(Offset(objectRva))-(long)headers.PEHeader.ImageBase;
        long setter=U32(Offset(table+12))-(long)headers.PEHeader.ImageBase;
        int codeOffset=Offset(setter);
        var fields=new List<int>();
        long? assignment=null;
        for(int i=0;i<320 && codeOffset+i+11<bytes.Length;i++)
        {
            int at=codeOffset+i;
            int size=bytes[at]==0x8d && bytes[at+1]==0x4f?3:
                bytes[at]==0x8d && bytes[at+1]==0x8f?6:0;
            if(size==0 || bytes[at+size]!=0xe8)continue;
            int field=size==3?bytes[at+2]:BitConverter.ToInt32(bytes,at+2);
            long target=setter+i+size+5+BitConverter.ToInt32(bytes,at+size+1);
            if(field<32 || field>1024)continue;
            assignment??=target;
            if(target==assignment)fields.Add(field);
        }
        if(fields.Count!=4 || fields.Where((field,index)=>field!=fields[0]+index*24).Any())return null;
        bool writable=headers.SectionHeaders.Any(section=>section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite)
            && objectRva>=section.VirtualAddress && objectRva+fields[3]+24<=(long)section.VirtualAddress+section.VirtualSize);
        return writable?(objectRva,fields.ToArray()):null;
    }

    private byte[] Read(long address,int length)
    {
        if(length==0)return [];
        byte[] bytes=new byte[length];
        if(address<=0 || !ReadProcessMemory(_process!,new nint(address),bytes,(nuint)length,out nuint read) || read!=(nuint)length)
            throw new IOException("QQ artwork metadata changed during its read");
        return bytes;
    }

    public void Dispose()=>_process?.Dispose();
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetExitCodeProcess(SafeProcessHandle handle,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool ReadProcessMemory(SafeProcessHandle handle,nint address,byte[] bytes,nuint size,out nuint read);
}
