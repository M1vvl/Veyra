using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;

namespace GlassDock.Music;

/// <summary>Locate the native producer of audioplayer.onPlayProgress in the installed image.</summary>
internal static class NeteaseProgressSource
{
    private static readonly byte[] ProducerPrefix=[0x0f,0x28,0xf2,0x0f,0x28,0xf9,0x48,0x8b,0xd9];
    private static readonly byte[] MessageName=Encoding.Unicode.GetBytes("audioplayer.onPlayProgress\0");
    private const int SignatureLength=85;
    private const int BlockSize=65536;

    internal static long? Locate(string path)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var image=new PEReader(stream,PEStreamOptions.LeaveOpen);
        var headers=image.PEHeaders;
        if(headers.CoffHeader.Machine!=Machine.Amd64 || headers.PEHeader is null)return null;
        byte[] buffer=new byte[BlockSize+SignatureLength-1];
        long? found=null;
        foreach(var section in headers.SectionHeaders)
        {
            if(!section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute))continue;
            for(int offset=0;offset<section.SizeOfRawData;offset+=BlockSize)
            {
                int length=Math.Min(buffer.Length,section.SizeOfRawData-offset);
                stream.Position=(long)section.PointerToRawData+offset;
                stream.ReadExactly(buffer.AsSpan(0,length));
                var bytes=buffer.AsSpan(0,length);
                int cursor=0;
                while(cursor<=length-SignatureLength)
                {
                    int match=bytes[cursor..].IndexOf(ProducerPrefix);
                    if(match<0)break;
                    cursor+=match;
                    if(cursor+SignatureLength>length)break;
                    long? address=Resolve(bytes.Slice(cursor,SignatureLength),
                        (long)section.VirtualAddress+offset+cursor,headers,stream);
                    if(address is not null)
                    {
                        // A different matching producer is ambiguous, so keep the fallback reader.
                        if(found is not null && found!=address)return null;
                        found=address;
                    }
                    cursor++;
                }
            }
        }
        return found;
    }

    private static long? Resolve(ReadOnlySpan<byte> code,long rva,PEHeaders headers,FileStream stream)
    {
        if(!code.Slice(9,4).SequenceEqual(new byte[] {0x66,0x0f,0x2e,0x0d})
            || !code.Slice(21,4).SequenceEqual(new byte[] {0x66,0x0f,0x2e,0x15})
            || !code.Slice(37,4).SequenceEqual(new byte[] {0xf2,0x0f,0x11,0x3d})
            || !code.Slice(45,4).SequenceEqual(new byte[] {0xf2,0x0f,0x11,0x35})
            || !code.Slice(68,3).SequenceEqual(new byte[] {0x48,0x8d,0x15}))return null;
        long position=rva+45+BitConverter.ToInt32(code.Slice(41,4));
        long buffered=rva+53+BitConverter.ToInt32(code.Slice(49,4));
        if(position!=rva+17+BitConverter.ToInt32(code.Slice(13,4))
            || buffered!=rva+29+BitConverter.ToInt32(code.Slice(25,4)) || buffered!=position+8)return null;
        bool writable=headers.SectionHeaders.Any(section=>
            section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemWrite)
            && position>=section.VirtualAddress && position+16<=(long)section.VirtualAddress+section.VirtualSize);
        if(!writable)return null;
        long name=rva+75+BitConverter.ToInt32(code.Slice(71,4));
        foreach(var section in headers.SectionHeaders)
        {
            long offset=name-section.VirtualAddress;
            if(offset<0 || offset+MessageName.Length>section.SizeOfRawData)continue;
            stream.Position=section.PointerToRawData+offset;
            Span<byte> text=stackalloc byte[MessageName.Length];
            stream.ReadExactly(text);
            return text.SequenceEqual(MessageName)?position:null;
        }
        return null;
    }
}
