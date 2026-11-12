using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.DXGI.DXGI;
using static Vortice.Direct3D11.D3D11;

namespace GlassDock.Rendering;

/// <summary>Independent recorder APIs, used only by the separate-process test fixture.</summary>
internal static class CaptureReadbackProbe
{
    internal sealed record Pixel(byte Red,byte Green,byte Blue);
    internal sealed record Request(long Window,int X,int Y);
    internal sealed record Result(Pixel? Screen,Pixel? Window,string? Error);
    internal static Result Read(Request request)
    {
        try
        {
            GetWindowRect((nint)request.Window,out var target);
            int x=target.Left+request.X,y=target.Top+request.Y;
            using var factory=CreateDXGIFactory1<IDXGIFactory1>();
            for(uint a=0;factory.EnumAdapters1(a,out var adapter).Success;a++)
            using(adapter)
            for(uint o=0;adapter.EnumOutputs(o,out var output).Success;o++)
            using(output)
            {
                var rect=output.Description.DesktopCoordinates;
                if(x<rect.Left || y<rect.Top || x>=rect.Right || y>=rect.Bottom)continue;
                D3D11CreateDevice(adapter,DriverType.Unknown,DeviceCreationFlags.BgraSupport,[FeatureLevel.Level_11_0],out ID3D11Device device,out ID3D11DeviceContext context).CheckError();
                using(device)using(context)
                {
                    Pixel? desktop=null;
                    using(var output1=output.QueryInterface<IDXGIOutput1>())
                    using(var duplication=output1.DuplicateOutput(device))
                    {
                        for(int attempt=0;attempt<20;attempt++)
                        {
                            var acquired=duplication.AcquireNextFrame(50,out _,out var resource);
                            if(acquired.Code==unchecked((int)0x887A0027))continue;
                            acquired.CheckError();
                            try { using(resource)using(var texture=resource.QueryInterface<ID3D11Texture2D>())desktop=ReadPixel(device,context,texture,x-rect.Left,y-rect.Top); }
                            finally { duplication.ReleaseFrame(); }
                            if(desktop is {Red:>180,Green:<80,Blue:>180})break;
                        }
                    }
                    Pixel? window=null;
                    using(var source=new WindowCaptureSource((nint)request.Window,device))
                    {
                        for(int attempt=0;attempt<75;attempt++)
                        {
                            source.Update(device);
                            if(source.Texture is { } texture) { window=ReadPixel(device,context,texture,request.X,request.Y); if(window is {Red:>180,Green:<80,Blue:>180})break; }
                            Thread.Sleep(20);
                        }
                    }
                    return new(desktop,window,null);
                }
            }
            return new(null,null,"No output contains the owned test window");
        }
        catch(Exception exception) { return new(null,null,exception.ToString()); }
    }
    private static Pixel ReadPixel(ID3D11Device device,ID3D11DeviceContext context,ID3D11Texture2D source,int x,int y)
    {
        using var staging=device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,1,1,mipLevels:1,bindFlags:BindFlags.None,usage:ResourceUsage.Staging,cpuAccessFlags:CpuAccessFlags.Read));
        context.CopySubresourceRegion(staging,0,0,0,0,source,0,new Box(x,y,0,x+1,y+1,1)); context.Flush();
        var mapped=context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
        try { return new(Marshal.ReadByte(mapped.DataPointer,2),Marshal.ReadByte(mapped.DataPointer,1),Marshal.ReadByte(mapped.DataPointer,0)); }
        finally { context.Unmap(staging,0); }
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out DesktopNative.Rect rect);
}
