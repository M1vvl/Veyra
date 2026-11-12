using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Mathematics;

namespace GlassDock.Rendering;

public sealed partial class GlassRenderer
{
    // Focused regression for the shipped Dock/sidebar shader, without touching
    // windows, user preferences, taskbar leases or notification registrations.
    internal static int RunShadowBoundaryProbe(string path)
    {
        var results=new List<object>(); bool passed=true;
        foreach(var sample in new[] {
            (Name:"dock",Width:640,Height:68,Scale:.65f),
            (Name:"dock-magnified",Width:800,Height:68,Scale:.65f),
            (Name:"sidebar",Width:74,Height:460,Scale:.8f),
            (Name:"sidebar-magnified",Width:90,Height:500,Scale:.8f),
            (Name:"dock-high-dpi",Width:960,Height:102,Scale:.975f),
            (Name:"sidebar-high-dpi",Width:111,Height:690,Scale:1.2f) })
        {
            using var renderer=new GlassRenderer();
            var region=new Int32Rect(100,100,sample.Width,sample.Height);
            renderer.EnsureDevice(region); renderer.EnsureSurfaces(region);
            using(var background=renderer._device!.CreateRenderTargetView(renderer._paintComposeRT!))
                renderer._context!.ClearRenderTargetView(background,new Color4(1,1,1,1));
            renderer._context!.GenerateMips(renderer._backgroundView!);
            var options=new GlassOptions {SurfaceInsetPixels=8*sample.Scale,CornerRadiusPixels=25*sample.Scale,LightTimeSeconds=0};
            var pixels=renderer.DrawValidationFrame(CompiledShaders.Value.Pixel,options);
            int Alpha(int x,int y)=>pixels[(y*sample.Width+x)*4+3];
            int edgeMax=0;
            for(int x=0;x<sample.Width;x++)edgeMax=Math.Max(edgeMax,Math.Max(Alpha(x,0),Alpha(x,sample.Height-1)));
            for(int y=0;y<sample.Height;y++)edgeMax=Math.Max(edgeMax,Math.Max(Alpha(0,y),Alpha(sample.Width-1,y)));
            int shadow=Alpha(Math.Max(1,(int)options.SurfaceInsetPixels-2),sample.Height/2);
            int center=Alpha(sample.Width/2,sample.Height/2);
            bool pass=edgeMax==0 && center==255 && shadow is >0 and <255;
            passed&=pass; results.Add(new {sample.Name,EdgeMaxAlpha=edgeMax,CenterAlpha=center,SoftShadowAlpha=shadow,Pass=pass});
            if(sample.Name is "dock" or "sidebar")
            {
                for(int pixel=0;pixel<pixels.Length;pixel+=4)
                {
                    int white=255-pixels[pixel+3];
                    for(int channel=0;channel<3;channel++)pixels[pixel+channel]=(byte)Math.Min(255,pixels[pixel+channel]+white);
                    pixels[pixel+3]=255;
                }
                var bitmap=BitmapSource.Create(sample.Width,sample.Height,96,96,PixelFormats.Bgra32,null,pixels,sample.Width*4);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var stream=File.Create(path+"."+sample.Name+".png"); encoder.Save(stream);
            }
        }
        File.WriteAllText(path,JsonSerializer.Serialize(new {Passed=passed,PrecompiledShaders=UsingPrecompiledShaders,Results=results},new JsonSerializerOptions {WriteIndented=true}));
        return passed?0:1;
    }
}
