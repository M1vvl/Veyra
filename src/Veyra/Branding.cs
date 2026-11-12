using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassDock;

internal static class Branding
{
    internal const string Name="Veyra";
    private static readonly Lazy<byte[]> IconBytes=new(()=> {
        using var stream=typeof(Branding).Assembly.GetManifestResourceStream("GlassDock.Assets.Veyra.ico")??throw new InvalidOperationException("Veyra icon is missing");
        using var bytes=new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
    });
    private static readonly Lazy<ImageSource> Image=new(()=> {
        using var stream=new MemoryStream(IconBytes.Value,false);
        var decoder=new IconBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var frame=decoder.Frames.OrderByDescending(item=>item.PixelWidth).First(); frame.Freeze(); return frame;
    });
    internal static ImageSource WindowIcon=>Image.Value;
    internal static nint CreateNativeIcon(int size=32)
    {
        using var stream=new MemoryStream(IconBytes.Value,false); using var reader=new BinaryReader(stream);
        reader.ReadUInt16(); reader.ReadUInt16(); int count=reader.ReadUInt16(),bestDistance=int.MaxValue,length=0,offset=0;
        for(int i=0;i<count;i++)
        {
            int width=reader.ReadByte(); if(width==0)width=256;
            reader.ReadBytes(7); int bytes=reader.ReadInt32(),position=reader.ReadInt32();
            int distance=Math.Abs(width-size); if(distance<bestDistance) {bestDistance=distance; length=bytes; offset=position;}
        }
        var pixels=IconBytes.Value.AsSpan(offset,length).ToArray();
        nint icon=CreateIconFromResourceEx(pixels,(uint)pixels.Length,true,0x00030000,size,size,0);
        if(icon==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法加载 Veyra 图标"); return icon;
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern nint CreateIconFromResourceEx(byte[] bits,uint size,[MarshalAs(UnmanagedType.Bool)] bool icon,uint version,int width,int height,uint flags);
}
