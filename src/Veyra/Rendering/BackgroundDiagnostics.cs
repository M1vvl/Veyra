using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlassDock.Rendering;

/// <summary>Bounded native background-source diagnostics; never used by the rendering loop.</summary>
internal static class BackgroundDiagnostics
{
    internal static int Run(string path)
    {
        var capture=SharedDesktopCapture.Acquire(new Int32Rect(0,0,100,100));
        var results=new List<object>();
        try
        {
            for(nint window=GetTopWindow(0);window!=0;window=GetWindow(window,2))
            {
                string name=Class(window);
                if(name is "WorkerW" or "Progman" or "CEF-OSC-WIDGET")Inspect(window,0);
            }
            void Inspect(nint window,int depth)
            {
                GetWindowRect(window,out var rect); GetWindowThreadProcessId(window,out uint process);
                object? pixels=null; string? error=null;
                if(IsWindowVisible(window) && rect.Right>rect.Left && rect.Bottom>rect.Top)
                try
                {
                    using var source=new WindowCaptureSource(window,capture.Device);
                    for(int attempt=0;attempt<40 && source.Texture is null;attempt++) { source.Update(capture.Device); Thread.Sleep(25); }
                    if(source.Texture is { } texture)
                    {
                        var description=texture.Description;
                        using var staging=capture.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,description.Width,description.Height,mipLevels:1,bindFlags:BindFlags.None,usage:ResourceUsage.Staging,cpuAccessFlags:CpuAccessFlags.Read));
                        capture.Context.CopyResource(staging,texture);
                        var map=capture.Context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            var samples=new List<object>();
                            for(int y=1;y<4;y++)for(int x=1;x<4;x++)
                            {
                                int offset=(int)(description.Height*y/4*map.RowPitch+description.Width*x/4*4);
                                samples.Add(new {r=Marshal.ReadByte(map.DataPointer,offset+2),g=Marshal.ReadByte(map.DataPointer,offset+1),b=Marshal.ReadByte(map.DataPointer,offset),a=Marshal.ReadByte(map.DataPointer,offset+3)});
                            }
                            pixels=new {description.Width,description.Height,samples};
                        }
                        finally { capture.Context.Unmap(staging,0); }
                    }
                    else error="No first capture frame";
                }
                catch(Exception exception) { error=exception.Message; }
                bool layered=GetLayeredWindowAttributes(window,out uint key,out byte alpha,out uint flags);
                results.Add(new {window=window.ToInt64(),depth,process,name=Class(window),style=GetWindowLong(window,-20),layered,key,alpha,flags,visible=IsWindowVisible(window),left=rect.Left,top=rect.Top,width=rect.Right-rect.Left,height=rect.Bottom-rect.Top,pixels,error});
                if(depth<3)for(nint child=GetTopWindow(window);child!=0;child=GetWindow(child,2))Inspect(child,depth+1);
            }
        }
        finally { capture.Release(); }
        File.WriteAllText(path,JsonSerializer.Serialize(new {time=DateTimeOffset.Now,results},new JsonSerializerOptions {WriteIndented=true})); return 0;
    }
    private static string Class(nint window) {var name=new StringBuilder(256); GetClassName(window,name,name.Capacity); return name.ToString();}
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint parent);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window,out uint key,out byte alpha,out uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window,out DesktopNative.Rect rect);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint window,StringBuilder name,int length);
}
